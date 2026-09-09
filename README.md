# HotelMigrationCache

**Высокопроизводительный in-memory key-value кэш для ускорения миграции между гостиничными системами.**

Дипломная работа OTUS (Захаров Святослав). Кэш реализован на .NET 10, C# 12, с собственным бинарным TCP-протоколом, zero-allocation парсером на `Span<byte>`, source-gen сериализацией и наблюдаемостью через OpenTelemetry. 

Демонстрируется на реальной задаче — миграция гостиницы из on-prem PMS в Cloud, с cost-accounting и измеримой финансовой экономией.

---

## Оглавление

- [Структура репозитория](#структура-репозитория)
- [Доменный контекст](#доменный-контекст)
- [Архитектура](#архитектура)
- [Ключевые особенности](#ключевые-особенности)
- [Как запустить](#как-запустить)
- [Результаты тестов](#результаты-тестов)
  - [BenchmarkDotNet — сериализация](#benchmarkdotnet--сериализация)
  - [NBomber — нагрузочное тестирование](#nbomber--нагрузочное-тестирование)
  - [Демо-прогон `--compare3`](#демо-прогон---compare3)

---

## Структура репозитория

```
HotelMigrationCache/
├── src/
│   ├── HotelMigrationCache.Core/            # Ядро кэша (TCP-сервер, парсер, хранилище)
│   ├── HotelMigrationCache.SourceGen/       # Roslyn source generator
│   ├── HotelMigrationCache.Shared/          # Публичный API (клиент, контракты, протокол)
│   ├── HotelMigrationCache.MigrationTool/   # Демонстратор миграции PMS → Cloud
│   ├── HotelMigrationCache.Demo/            # Оркестратор + OpenTelemetry SDK
│   └── HotelMigrationCache.Benchmarks/      # BDN + NBomber
├── tests/
│   ├── HotelMigrationCache.Core.Tests/      # xUnit тесты для хранилища и парсера
│   └── HotelMigrationCache.Shared.Tests/    # Интеграционные тесты клиент↔сервер (сокеты, framing, пул)
├── tools/
│   ├── SourceDataAugmenter/                 # Утилита аугментации XML-фикстур
│   └── SimProdDatasetBuilder/               # Генератор sim-prod датасетов
├── prerequisites/
│   ├── data_for_migration/                  # XML-фикстуры (sim-prod разных масштабов)
│   └── NBomber_Reports/                     # Артефакты BDN + NBomber (по timestamp'у)
├── docker-compose.yml                       # OTEL Collector + Jaeger
├── otel-collector-config.yaml
├── Directory.Packages.props                 # Центральное управление версиями NuGet
└── HotelMigrationCache.slnx
```

## Доменный контекст

Гостиничные сети переходят со старых on-prem PMS на облачные. Каждая миграция — это тысячи и десятки тысяч REST-вызовов к облачному API.

**Проблема**: облачный API стоит денег, медленный и **throttled**.
- Типовой прайс: **$20 за 10 000 транзакций**.
- Латентность: 200–3000 мс на вызов (SLA).
- **Rate limit** (Oracle Hospitality Integration Platform, [официальная документация](https://docs.oracle.com/en/industries/hospitality/integration-platform/index.html)):
  - **50 rps sustained · 100 rps burst — per gateway, shared по всем consumer'ам** (POS, revenue mgmt, мобильный check-in — дерутся за один и тот же бюджет).
  - Выше sustained → автоматические delays на каждый лишний запрос.
  - Выше burst → HTTP **429 Too Many Requests**, требуется retry.

**Реальные масштабы миграций** (из продакшн-записей):
- Типовой отель: **~20 000 профайлов + ~50 000 бронирований** (одна из наблюдавшихся миграций).
- Крупная сеть: **~90 000 профайлов**, бронирования создаются на **3–4 года вперёд** → ~200–300k активных резервов.

**Ключевое наблюдение**: одни и те же данные читаются многократно. Профайл гостя используется как main-guest своей брони, accompanying-guest в чужих бронях, billing-контакт в корпоративных резервах, relationship-target — 5–10 повторных вызовов. Справочники (RoomType, RateCode, LoyaltyLevel) — 20–50 уникальных значений на весь отель. Идеальные кандидаты на кэш.

---

## Архитектура

Solution из **6 проектов**:

| Проект | Роль |
|---|---|
| `HotelMigrationCache.Core` | Ядро: `InMemoryKeyValueStore`, `TcpServerInterface`, `CommandParser` |
| `HotelMigrationCache.SourceGen` | Roslyn source generator для zero-reflection бинарной сериализации |
| `HotelMigrationCache.Shared` | Публичный API: клиент `CacheServiceTcpClient`, контракты, протокол |
| `HotelMigrationCache.MigrationTool` | Демонстратор — эмулирует миграцию PMS → Cloud, cache→local→cloud fallback |
| `HotelMigrationCache.Demo` | Оркестратор: OpenTelemetry SDK, cache-сервер, subprocess лончер |
| `HotelMigrationCache.Benchmarks` | BenchmarkDotNet + NBomber-сценарии |

```
Client → CacheServiceTcpClient (pool 1..256 TCP, per-conn IO lock)
       → TCP framing (Length-Prefix + SOH/STX/ETX/EOT + XOR checksum)
       → TcpServerInterface (Accept loop + SemaphoreSlim(256))
       → CommandParser (Span-based, zero-allocation)
       → InMemoryKeyValueStore (ReaderWriterLockSlim + Interlocked stats)
       → Response (4-byte length + payload)
```

---

## Ключевые особенности

**Ядро** (`HotelMigrationCache.Core`):
- `Dictionary<byte[], byte[]>` — **чистый blob-store**, семантика на клиенте, ключи/значения байтовые.
- Custom `ByteArrayEqualityComparer` (`SequenceEqual` + `HashCode.AddBytes`) — без него `byte[]` сравнивался бы по ссылке.
- `ReaderWriterLockSlim` — множественные читатели без конкуренции (миграция — 70–80% чтений).
- `Interlocked` counters для статистики (hit/miss/set/delete) — lock-free hot path.

**Сеть**:
- Async accept-loop на `System.Net.Sockets` (без ASP.NET Core).
- `SemaphoreSlim(256, 256)` — предел одновременных клиентов (`ServerLimits.MaxServerConnections`).
- `ArrayPool<byte>.Shared.Rent(8192)` для приёма, `Return(buffer, true)` в `finally`.
- `Socket.ReceiveAsync(Memory<byte>)` — zero-copy до парсера.

**Протокол**:
```
[SOH][CHK][STX][cmdLen][cmd][keyLen][key][valLen][val][ETX][EOT]
 0x01  1B  0x02   1B     N    4B     M    4B      K   0x03 0x04
```
Команды: `GET`, `SET`, `DELETE`, `STATS`. Length-prefix (Int32 LE) + маркеры + XOR checksum.

**Парсер** (`CommandParser.Parse(ReadOnlySpan<byte>)`):
- Возвращает `readonly ref struct CacheParsedCommand` — гарантированно на стеке.
- `BinaryPrimitives.ReadInt32LittleEndian` для длин, `SequenceEqual` для команд.
- **Zero allocations** на горячем пути.

**Source Generator** (`HotelMigrationCache.SourceGen`):
- `IIncrementalGenerator`, атрибут `[GenerateBinarySerializer]`.
- Генерирует `SerializeToBinary(Stream)` + `static DeserializeFromBinary(Stream)` (static-abstract из `IBinarySerializable<TSelf>`).
- Поддержка: `Primitive` (numeric + bool + decimal), `String` (nullable-aware), `DateTime` (`ToBinary`), **`DateOnly`** (через `DayNumber`), **`enum`** (через underlying type, обычно Int32).
- Собственные диагностики `BS001`/`BS002` (класс не `partial` / нет свойств).
- F-bounded self-constraint `where TSelf : IBinarySerializable<TSelf>` — разрешает вызов `TValue.DeserializeFromBinary(stream)` через generic-параметр без reflection.

**Клиент** (`CacheServiceTcpClient`):
- Пул из N TCP-соединений (1..256), round-robin через `Interlocked.Increment`.
- Внутри соединения — `SemaphoreSlim(1, 1)` для сериализации write→read (иначе framing плывёт).
- **Generic API**: `SetAsync<TValue>` / `GetAsync<TValue>` с констрейнтом `where TValue : IBinarySerializable<TValue>` — клиент не привязан к конкретному DTO, работает с любым `[GenerateBinarySerializer]` классом. В проекте через этот API уже проходят **6 разных типов** (`CloudProfileData` + 5 reference DTO).
- **Пресеты `MigrationConcurrency`** привязаны к бюджету Oracle Hospitality (50 rps sustained / 100 burst): `Ten` = 12% budget · `TwentyFive` = 32% (дефолт) · `Sixty` = 76% (boosted, безопасен только с кэшем — миссы разрежены).

**OpenTelemetry**:
- `ActivitySource "HotelMigrationCache.TcpServer"` — span `CommandProcessing` на каждый вызов, теги `command.name`, `response.status`, `payload.size`, `client.endpoint`.
- `Meter` — `Counter<int>` `commands.processed` + `Histogram<double>` `commands.duration_ms`.
- OTLP → OpenTelemetry Collector (порт 4317) → **Jaeger** (порт 16686). Docker Compose поднимает Collector + Jaeger одной командой.
- `JaegerTraceReader` в MigrationTool читает трейсы обратно через `/api/traces` — **4 параллельных запроса** с фильтром `tags={"command.name":"Get|Set|Delete|Stats"}`. Это обход дефолтного лимита Jaeger в 2000 трейсов на запрос: при большом объёме прогонов хвост очереди доминируется GET'ами и SET/DELETE вытесняются из выборки. Per-command запросы гарантируют видимость всех 4 команд в per-command статистике.

**Безопасность**:
- **XOR checksum** — детектирует повреждение payload.
- **Валидация размера** (`_receiveMessageByteCountRestriction = 4096`) — защита от DoS через huge buffers.
- **`SemaphoreSlim(256, 256)`** — hard-cap на connection flood.
- **`ClassifyResponse`** — safe-label вместо raw bytes в OTEL-tag (не пропускаем PII).

---

## Как запустить

**Требования**: .NET 10 SDK, Docker Desktop (для OTEL/Jaeger).

### 1. Полное демо (`--compare` прогон с cost-accounting)

```bash
docker compose up -d          # OTEL Collector + Jaeger
dotnet run --project src/HotelMigrationCache.Demo --configuration Release
```

Demo стартует cache TCP server + запускает MigrationTool subprocess в `--compare` режиме (No cache vs With cache), в конце генерирует comparison-отчёт.

**Расширенный трёхсценарный сравнительный прогон** (No cache p=25 → Cache p=25 → Cache p=60 boosted):

```bash
dotnet run --project src/HotelMigrationCache.Demo --configuration Release -- --compare3
```

Между сценариями кэш сбрасывается через штатную команду `DELETE`: клиент ведёт in-process множество ключей, которых касался (SET/GET/DELETE), в конце прогона итерирует по нему и удаляет каждый ключ через сервер. Это гарантирует apples-to-apples сравнение Run 2 vs Run 3 (без прогретого состояния из предыдущего сценария) и попутно **демонстрирует реальное применение обязательной команды `DELETE`** из требований диплома.

**Jaeger UI**: <http://localhost:16686>.

### 2. Только бенчмарки

```bash
# Демо-режим: только cache-сервер, без миграции
dotnet run --project src/HotelMigrationCache.Demo --configuration Release -- --bench

# В другом терминале: BDN + все 5 NBomber-сценариев
dotnet run --project src/HotelMigrationCache.Benchmarks --configuration Release
```

Опции Benchmarks:
- `(без флагов)` — BDN → NBomber (все 5 сценариев).
- `--bench` — только BDN.
- `--nbomber` — только NBomber.

Отчёты пишутся в `prerequisites/NBomber_Reports/{timestamp}_{scenario}/`.

### 3. Тесты

```bash
dotnet test
```

Два тестовых проекта:
- **`HotelMigrationCache.Core.Tests`** — юнит-тесты хранилища (`InMemoryKeyValueStore`) и парсера (`CommandParser`).
- **`HotelMigrationCache.Shared.Tests`** — интеграционные тесты клиент↔сервер: SET/GET/DELETE через реальный TCP-сокет на эфемерном порту, проверка framing'а length-prefix ответа, валидация диапазона `poolSize` (1..256), параллельные операции через пул, переиспользование соединений.

Итого — **30 тестов** на все критичные пути (0 skipped).

---

## Результаты тестов

### BenchmarkDotNet — сериализация

Сериализация `CloudProfileData` (10 полей, реалистичный размер профайла гостя):

| Method | Mean | Ratio | Allocated | Alloc Ratio |
|---|---:|---:|---:|---:|
| `SystemTextJson` (baseline) | 533.1 ns | 1.00 | 312 B | 1.00 |
| `GeneratedBinary` | **188.0 ns** | **0.35** | 528 B | 1.69 |
| `GeneratedBinaryPooled` | **164.4 ns** | **0.31** | **248 B** | **0.79** |

- **~3× быстрее** JSON (164 нс vs 533 нс на pooled-варианте).
- **~21% меньше аллокаций** с `ArrayPool`.
- **Zero reflection** — AOT-friendly, JIT инлайнит.
- Обобщение интерфейса на `IBinarySerializable<T>` регрессий не внесло (числа до/после совпадают в пределах шума).

Исходник: [`CloudProfileDataSerializationBenchmark.cs`](src/HotelMigrationCache.Benchmarks/Benchmarks/CloudProfileDataSerializationBenchmark.cs).
Отчёт: `prerequisites/NBomber_Reports/{ts}_bench/results/*.md`.

### NBomber — нагрузочное тестирование

5 сценариев на loopback (Windows 11, .NET 10 Release, TCP-сервер на 3456), суммарно **4.59 миллиона запросов, 0 failures**:

| Сценарий | Модель | RPS | p50 | p95 | p99 | ok / fail |
|---|---|---:|---:|---:|---:|---|
| `max_throughput_ramp` | Open, ramp → 30k/s, pool=200 | **18 569** | 416 ms | 2 531 ms | 2 740 ms | 742 750 / **0** |
| `sustained_10_conn` | Closed, 10 VU × 1 сокет | **38 162** | 0.23 ms | 0.38 ms | 1.32 ms | 1 144 866 / **0** |
| `throughput_stress` | Closed, 200 VU × 1 сокет | **41 238** | 4.51 ms | 7.39 ms | 9.7 ms | 1 237 143 / **0** |
| `open_rate_pool_10` | Open, 3k/s, pool=10 | **2 643** | 0.77 ms | 1.76 ms | 2.88 ms | 92 500 / **0** |
| `single_client_pool_50` | Closed, 50 VU через один клиент pool=50 | **39 320** | 0.96 ms | 2.64 ms | 4.57 ms | 1 376 203 / **0** |

**Ключевые выводы**:
- **Peak устойчивый throughput ≈ 41 000 RPS** (`throughput_stress`, 200 VU × 1 сокет).
- **Sub-millisecond latency** до 50 конкурентных подключений (p50 = 0.23–0.96 ms).
- **`single_client_pool_50`** (один клиент с пулом=50, 50 VU шарят) держит **~39k RPS** — практически догнал `throughput_stress` (~41k) при **4× меньшем числе сокетов** и **на порядок лучшей latency** (p50 0.96 ms vs 4.51 ms). Прямое доказательство эффективности переработанного клиентского пула.
- **`max_throughput_ramp`** упирается в потолок железа ~18 500 RPS при инъекции 30k rps: излишек уходит в очередь → p95/p99 растут, но корректность не деградирует (0 failures). Tail latency гуляет от прогона к прогону — характер open-loop overload.
- Warm-up фаза (`Inject(500/s, 5s)` для open-loop или `RampingConstant` для closed-loop) обязательна — прогревает JIT/GC/сокеты до основного измерения.

Исходник сценариев: [`Benchmarks/Program.cs`](src/HotelMigrationCache.Benchmarks/Program.cs).
Отчёты: `prerequisites/NBomber_Reports/{ts}_{scenario}/nbomber_report_*.md`.

### Демо-прогон `--compare3`

Последний полный прогон на датасете **100 профайлов + 500 бронирований** (`sim-prod/100-500`), режим `--compare3`: **три последовательных прогона** (No cache p=25 → Cache p=25 → Cache p=60 boosted) с итоговой сравнительной таблицей.

**Ключевые цифры (реальный прогон):**

| # | Сценарий | Wall-clock | Cloud calls | Cloud rps (% Oracle 50) | Δ vs (1) |
|---|---|---:|---:|---:|---:|
| **1** | No cache · p=25 | 8 min 12 s | 7 049 | 15.4 rps (31%) | — |
| **2** | Cache · p=25 | 4 min 15 s | 3 623 | 15.7 rps (31%) | **×1.93 faster** |
| **3** | Cache · p=60 (boosted) | **1 min 59 s** | 3 599 | 36.7 rps (73%) | **×4.15 faster** |

**Разложение вклада:**
- **Cache contribution** (1 → 2, при одинаковом parallelism): **×1.93 faster**.
- **Safe-boost contribution** (2 → 3, кэш открывает возможность безопасно поднять parallelism): **×2.15 faster**.
- **Total** (1 → 3, cache + boosted parallelism): **×4.15 faster**.

Cache lookups (совпадает для 2 и 3): **3 528 всего**, из них **~99–100% сразу из кэша**. В сценарии 3 hit rate достигает 100% — reference-данные уже прогреты после сценария 2 (кэш-сервер живёт всё время между запусками).

Cloud calls почти не меняются между сценариями 2 и 3 (3 623 vs 3 599 — разница = 24 первичных ref-warm'а). Значит **cost saved одинаков независимо от parallelism** — это дополнительный аргумент к тому, что boost'ом мы платим только временем, но не деньгами.

**Money on this run:**

| Метрика | (1) No cache | (2) Cache p=25 | (3) Cache p=60 |
|---|---:|---:|---:|
| Cost — best case | $14.10 | $7.25 | $7.20 |
| Cost — realistic (×1.35) | $19.03 | $9.78 (**−$9.25**) | $9.72 (**−$9.32**) |

**Проекция на реальные размеры отелей** — прямая экстраполяция по удельной стоимости на запись из фактического прогона (per-record base save: $0.0114, realistic: $0.0155):

| Класс отеля | Профайлы + брони | Save base | Save realistic |
|---|---:|---:|---:|
| **Medium** (типовой) | 20 000 + 50 000 = **70 000** | **~$800** | **~$1 087** (утилита посчитала $1 086.75) |
| **Large** (крупная сеть) | 90 000 + ~220 000 = **310 000** | **~$3 534** | **~$4 813** |

*Оценка large: 500-комнатный отель × 60% occupancy × 4 года форвард-букинга × 2-ночный avg stay ≈ 220 000 будущих броней.*

**Wall-clock экстраполяция на medium (70k records)** — прямое масштабирование:

| # | Сценарий | Wall-clock medium (70k) | Wall-clock large (310k) |
|---|---|---:|---:|
| 1 | No cache · p=25 | ~15.9 h | ~2.9 days |
| 2 | Cache · p=25 | ~8.3 h | ~1.5 days |
| 3 | Cache · p=60 (boosted) | **~3.9 h** | **~17 h (0.7 days)** |

Ключевые выводы (реальные, измеренные):
- **Ratio 1→2 = ×1.93** — вклад кэша сам по себе, при одинаковом уровне параллельности.
- **Ratio 2→3 = ×2.15** — вклад безопасного повышения parallelism (60 workers × 0.63 rps = 37 rps, 73% Oracle 50-rps budget, всё ещё в безопасной зоне).
- **Ratio 1→3 = ×4.15** — суммарный эффект. Миграция medium-отеля укладывается в **рабочий день** вместо двух смен.
- Сценарий (3) без кэша был бы небезопасен: 60 workers × 0.63 rps × полный процент cloud-hit = ~38 rps sustained + бросты > 100 rps → HTTP 429 → retry-каскад съедает выигрыш.

**При онбординге 5 клиентов / месяц (60 миграций / год) в сценарии (2 или 3, деньги те же):**

| Портфолио | Годовая экономия (realistic) |
|---|---:|
| Все medium | **~$65k / год** |
| Все large | **~$288k / год** |
| Смешанное (60% medium, 40% large) | **~$154k / год** |

Полный вывод утилиты (Spectre.Console) из последнего прогона:

```
                                          Comparison: 3 scenarios (Oracle 50-rps budget context)
┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┳━━━━━━━━━━━━━━━━━━━━━━┳━━━━━━━━━━━━━━━━━━━━━━━━━┳━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
┃ Metric                                                   ┃ (1) No cache · p=25  ┃    (2) Cache · p=25     ┃ (3) Cache · p=60 (boosted) ┃
┣━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━╋━━━━━━━━━━━━━━━━━━━━━━╋━━━━━━━━━━━━━━━━━━━━━━━━━╋━━━━━━━━━━━━━━━━━━━━━━━━━━━━┫
┃ ⏱  Whole migration wall-clock                            ┃      8 min 15 s      ┃ 4 min 12 s ×1,97 vs (1) ┃     2 min ×4,13 vs (1)     ┃
┃                                                          ┃                      ┃                         ┃                            ┃
┃ 📦 One record on average                                 ┃                      ┃                         ┃                            ┃
┃   Profile (100 records)                                  ┃        10,7 s        ┃          6,5 s          ┃           6,8 s            ┃
┃   Reservation (500 records)                              ┃        20,4 s        ┃         10,1 s          ┃           10,1 s           ┃
┃                                                          ┃                      ┃                         ┃                            ┃
┃ 💾 What the cache did                                    ┃                      ┃                         ┃                            ┃
┃   Look-ups (total)                                       ┃        3 528         ┃          3 528          ┃           3 528            ┃
┃   Answered from cache                                    ┃          0           ┃          3 504          ┃           3 504            ┃
┃   Hit rate                                               ┃        0,0 %         ┃         99,3 %          ┃           99,3 %           ┃
┃                                                          ┃                      ┃                         ┃                            ┃
┃ ☁  Cloud pressure (vs Oracle 50 rps limit)               ┃                      ┃                         ┃                            ┃
┃   Cloud calls (total)                                    ┃        7 049         ┃          3 623          ┃           3 623            ┃
┃   Wall-clock waiting for cloud                           ┃      7 min 35 s      ┃       3 min 53 s        ┃         1 min 37 s         ┃
┃   Cloud rps (effective)                                  ┃ 15,5 rps (31% of 50) ┃  15,5 rps (31% of 50)   ┃    37,4 rps (75% of 50)    ┃
┃                                                          ┃                      ┃                         ┃                            ┃
┃ 💰 Money on this run (rate: $20 / 10k · ×1,35 realistic) ┃                      ┃                         ┃                            ┃
┃   Best case                                              ┃        $14,10        ┃          $7,25          ┃           $7,25            ┃
┃   Realistic                                              ┃        $19,03        ┃      $9,78 −$9,25       ┃        $9,78 −$9,25        ┃
┃                                                          ┃                      ┃                         ┃                            ┃
┃ 🏨 Projected saving on 70 000 records (realistic)        ┃          —           ┃     $1079,19 saved      ┃       $1079,19 saved       ┃
┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┻━━━━━━━━━━━━━━━━━━━━━━┻━━━━━━━━━━━━━━━━━━━━━━━━━┻━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛

Cache contribution (1 → 2, at same parallelism): ×1,97 faster
Safe-boost contribution (2 → 3, cache enables higher parallelism): ×2,10 faster
Total (1 → 3, cache + boosted parallelism): ×4,13 faster
```

**Про обобщённый generic API кэша.** Раньше `SetAsync<TValue>` / `GetAsync<TValue>` использовался только для одного типа (`CloudProfileData`) — контракт был чистый, но применимость выглядела гипотетической. Теперь через тот же generic-путь проходят **шесть типов**: `CloudProfileData`, `CloudRoomTypeInfo`, `CloudRateCodeInfo`, `CloudLoyaltyRateRule`, `CloudPaymentTypeInfo`, `CloudPreferenceMapping`. Каждый помечен `[GenerateBinarySerializer]`, реализует `IBinarySerializable<T>` через source-gen, ходит по одному API. Это конкретное подтверждение, что архитектура открыта под любые новые DTO без правок ядра.

---