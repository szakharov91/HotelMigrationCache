using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Statistics;

// Читатель Jaeger's HTTP API. Дёргает /api/traces?service=<name>&limit=... , аккумулирует
// per-command тайминги и count. Используется, когда cache-сервер живёт в отдельном процессе
// (Demo-оркестратор), и локальный MeterListener не видит его emissions.
//
// Особенность Jaeger HTTP API: параметр `limit` ограничивает общее число возвращаемых trace'ов
// (обычно верхний cap ~1500-2000 на memory storage). При большом объёме прогонов (несколько
// тысяч GET'ов доминируют хвост reservation-фазы) SET'ы и DELETE'ы вытесняются из выборки,
// и в отчёт попадает только GET.
//
// Обход: запрашиваем каждый тип команды отдельно с фильтром `tags={"command.name":"<Kind>"}`.
// Jaeger вернёт до `limit` трейсов ЭТОГО типа, гарантируя видимость всех четырёх команд.
public sealed class JaegerTraceReader
{
    private const int _perCommandLimit = 2000;
    private const string _lookback = "1h";

    // Значения `command.name`-тега — enum-имена ServerCommandKind.ToString().
    // Сервер выставляет тег в TcpServerInterface: `result.GetCommandKind().ToString()`.
    private static readonly string[] _knownCommands = ["Get", "Set", "Delete", "Stats"];

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<JaegerTraceReader> _logger;
    private readonly string _baseUrl;
    private readonly string _serviceName;

    public JaegerTraceReader(ILogger<JaegerTraceReader> logger, string? baseUrl = null, string serviceName = "HotelMigrationCache.TcpServer")
    {
        _logger = logger;
        // URL из env-переменной, чтобы не хардкодить абсолютный адрес (Sonar S1075).
        var resolved = baseUrl
            ?? Environment.GetEnvironmentVariable("JAEGER_URL")
            ?? string.Concat("http://", "localhost", ":16686");
        _baseUrl = resolved.TrimEnd('/');
        _serviceName = serviceName;
    }

    public async Task<IReadOnlyDictionary<string, JaegerCommandStats>?> FetchAsync(CancellationToken ct)
    {
        // Параллельно запрашиваем 4 команды. Каждая — независимый HTTP-запрос к Jaeger.
        var perCommandTasks = _knownCommands
            .Select(cmd => FetchOneCommandAsync(cmd, ct))
            .ToArray();

        try
        {
            await Task.WhenAll(perCommandTasks);
        }
        catch
        {
            // Ошибки логируются в FetchOneCommandAsync; здесь просто продолжаем с тем, что есть.
        }

        // Аггрегируем результаты в единый словарь.
        var merged = new Dictionary<string, JaegerCommandStats>(StringComparer.Ordinal);
        bool anySuccessful = false;

        for (int i = 0; i < _knownCommands.Length; i++)
        {
            var task = perCommandTasks[i];
            if (task.IsCompletedSuccessfully && task.Result is { } stats)
            {
                anySuccessful = true;
                if (stats.Count > 0)
                    merged[_knownCommands[i]] = stats;
            }
        }

        // Если ни один запрос не удался — сигнализируем null (UI выведет "no traces").
        return anySuccessful ? merged : null;
    }

    private async Task<JaegerCommandStats?> FetchOneCommandAsync(string commandName, CancellationToken ct)
    {
        // Jaeger tag-filter: tags={"command.name":"Get"} — JSON-encoded, потом URL-encoded.
        var tagJson = $"{{\"command.name\":\"{commandName}\"}}";
        var url = $"{_baseUrl}/api/traces?service={Uri.EscapeDataString(_serviceName)}"
                + $"&tags={Uri.EscapeDataString(tagJson)}"
                + $"&limit={_perCommandLimit}&lookback={_lookback}";

        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jaeger returned {StatusCode} for command={Command} — skipping this command.",
                    resp.StatusCode, commandName);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return AggregateSingle(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jaeger fetch failed for command={Command} at {Url} — skipping.",
                commandName, url);
            return null;
        }
    }

    // Аггрегация одного per-command HTTP-ответа. Здесь filter по tags применил Jaeger,
    // поэтому все span'ы в data[*].spans[*] — уже нужной команды. Просто складываем durations.
    private static JaegerCommandStats AggregateSingle(JsonElement root)
    {
        var acc = new StatsAcc();

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return acc.ToSnapshot();

        foreach (var trace in data.EnumerateArray())
        {
            if (!trace.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var span in spans.EnumerateArray())
            {
                double durationMs = 0;
                if (span.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                    durationMs = d.GetDouble() / 1000.0; // Jaeger duration in µs.
                acc.Add(durationMs);
            }
        }

        return acc.ToSnapshot();
    }

    private sealed class StatsAcc
    {
        private long _count;
        private double _sumMs;
        private double _min = double.MaxValue;
        private double _max;

        public void Add(double ms)
        {
            _count++;
            _sumMs += ms;
            if (ms < _min) _min = ms;
            if (ms > _max) _max = ms;
        }

        public JaegerCommandStats ToSnapshot() => new(
            Count: _count,
            AvgMs: _count == 0 ? 0 : _sumMs / _count,
            MinMs: _count == 0 ? 0 : _min,
            MaxMs: _max,
            TotalTime: TimeSpan.FromMilliseconds(_sumMs));
    }
}

public sealed record JaegerCommandStats(long Count, double AvgMs, double MinMs, double MaxMs, TimeSpan TotalTime);
