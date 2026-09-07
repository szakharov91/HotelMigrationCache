using System.Collections.Concurrent;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using HotelMigrationCache.Benchmarks.Benchmarks;
using HotelMigrationCache.Benchmarks.Utils;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Utils;
using NBomber.Contracts;
using NBomber.CSharp;

namespace HotelMigrationCache.Benchmarks;

public static class Program
{
    private const string _host = "127.0.0.1";
    private const int _port = 3456;
    private const int _openPoolSize = 10;
    private const int _singleClientPoolSize = 50;
    private const int _maxThroughputPoolSize = 200;

    // Общий низкорейтовый warm-up для сценариев с pooled-клиентом.
    // Прогревает JIT/GC/сокеты без искажений измерений (5с × 500 rps = 2500 запросов на разогрев).
    private static readonly LoadSimulation _pooledWarmUp =
        Simulation.Inject(rate: 500, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(5));

    /// <summary>Постоянное подключение на каждого виртуального пользователя (VU).
    /// Ключ — уникальный InstanceId копии в рамках одной NBomber-сессии. </summary>
    private static readonly ConcurrentDictionary<string, CacheServiceTcpClient> _clients = new();

    /// <summary> Один клиент со встроенным пулом из N соединений (round-robin + per-slot io lock внутри клиента).
    /// Используется в разомкнутой модели и в сценарии single_client_pool_50. </summary>
    private static CacheServiceTcpClient? _pooledClient;

    public static async Task Main(string[] args)
    {
        var skipNBomber = args.Contains("--bench");
        var skipBenchmarks = args.Contains("--nbomber");

        // BenchmarkDotNet прогоняем ДО NBomber-сценариев: чистые микробенчмарки
        // (сериализация профайла) не должны конкурировать с прогретым сервером за CPU/GC.
        // Артефакты BDN пишем в ту же корневую папку отчётов, отдельным подкаталогом.
        if (!skipBenchmarks)
            RunBenchmarks();

        if (skipNBomber)
            return;

        Console.WriteLine("Hello NBomber!");
        Console.WriteLine($"Waiting for server on tcp://{_host}:{_port} ...");
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Сценарий 0 (max_throughput_ramp): open-loop поиск потолка кэша.
        // Прогрев Inject(500/s, 5s) → линейный ramp до 30 000 rps за 20с → удержание 15с на пике.
        // Клиент с пулом на 200 сокетов — client-side не bottleneck.
        // Идея: увидеть, где latency начинает деградировать при постоянно растущем rps —
        // это и есть "как быстро кэш реально может".
        var maxThroughput = Scenario.Create("max_throughput_ramp", ExecuteWithPooledClient)
            .WithLoadSimulations(
                _pooledWarmUp,
                Simulation.RampingInject(rate: 30_000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(20)),
                Simulation.Inject(rate: 30_000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(15))
            );

        // Сценарий 1: рабочий профиль — 10 постоянных подключений,
        // каждое переиспользует TCP-сессию. Показывает устойчивый RPS
        // при реальной ожидаемой параллельности.
        // Warm-up через WithWarmUpDuration (не Inject) — closed-loop KeepConstant, тот же контур VU,
        // и Execute кладёт клиента в _clients по InstanceId. Inject-прогрев создал бы эфемерных
        // VU с новыми InstanceId и оставил бы утёкшие CacheServiceTcpClient в _clients.
        var sustained = Scenario.Create("sustained_10_conn", Execute)
            .WithLoadSimulations(
                Simulation.KeepConstant(copies: 10, during: TimeSpan.FromSeconds(30))
            )
            .WithWarmUpDuration(TimeSpan.FromSeconds(5));

        // Сценарий 2: пиковая пропускная способность — много параллельных VU,
        // каждый со своим долгоживущим подключением. Ищет верхнюю границу RPS
        // самого кэша, а не сокет-акробатики. Warm-up см. коммент к sustained.
        var stress = Scenario.Create("throughput_stress", Execute)
            .WithLoadSimulations(
                Simulation.KeepConstant(copies: 200, during: TimeSpan.FromSeconds(30))
            )
            .WithWarmUpDuration(TimeSpan.FromSeconds(5));

        // Сценарий 3: разомкнутая модель — фиксированная скорость инъекции
        // (rate rps независимо от того, завершились ли предыдущие запросы),
        // против одного клиента с встроенным пулом из 10 подключений
        // (штатное API CacheServiceTcpClient(poolSize: 10) — как в MigrationTool).
        // Warm-up фазой Inject(500/s, 5s) — эфемерные VU шарят один _pooledClient, утечки нет.
        var openRate = Scenario.Create("open_rate_pool_10", ExecuteWithPooledClient)
            .WithLoadSimulations(
                _pooledWarmUp,
                Simulation.Inject(rate: 3000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
            );

        // Сценарий 4: замкнутая модель через один клиент с пулом на 50 соединений.
        // 50 VU шарят один CacheServiceTcpClient — round-robin PickIndex + per-slot io lock
        // внутри клиента обеспечивают безопасную конкурентность. Показывает, что штатный пул
        // клиента держит нагрузку эквивалентно 50 отдельным клиентам (как в throughput_stress),
        // но через единый экземпляр — прямой аналог production-использования в MigrationTool.
        // Warm-up через RampingConstant: closed-loop ramp 0→50 VU за 5с (NBomber запрещает
        // миксовать open-model Inject с closed-model KeepConstant в одном сценарии).
        var singleClientPool = Scenario.Create("single_client_pool_50", ExecuteWithPooledClient)
            .WithLoadSimulations(
                Simulation.RampingConstant(copies: _singleClientPoolSize, during: TimeSpan.FromSeconds(5)),
                Simulation.KeepConstant(copies: _singleClientPoolSize, during: TimeSpan.FromSeconds(30))
            );

        // Каждый сценарий запускаем отдельной сессией, чтобы отчёты
        // не смешивались и load одного не давил на измерения другого.
        InitPooledClient(_maxThroughputPoolSize);
        Run(maxThroughput, "max_throughput");
        DisposePooledClient();

        Run(sustained, "sustained");
        DisposeClients();

        Run(stress, "stress");
        DisposeClients();

        InitPooledClient(_openPoolSize);
        Run(openRate, "open_rate");
        DisposePooledClient();

        InitPooledClient(_singleClientPoolSize);
        Run(singleClientPool, "single_client_pool");
        DisposePooledClient();

        Console.WriteLine("Done. Press <Enter> to exit.");
        Console.ReadLine();
    }

    private static void Run(ScenarioProps scenario, string label)
    {
        var reportsRoot = GetReportsRoot();

        var runner = NBomberRunner.RegisterScenarios(scenario);
        if (Directory.Exists(reportsRoot))
        {
            runner = runner.WithReportFolder(
                Path.Combine(reportsRoot, $"{DateTime.Now:yyyy_MM_dd-HH_mm_ss}_{label}")
            );
        }

        runner.Run();
    }

    private static void RunBenchmarks()
    {
        var reportsRoot = GetReportsRoot();
        // BDN сам создаёт подпапки под logs/results — достаточно указать корневой ArtifactsPath.
        var artifactsPath = Path.Combine(reportsRoot, $"{DateTime.Now:yyyy_MM_dd-HH_mm_ss}_bench");

        var config = DefaultConfig.Instance.WithArtifactsPath(artifactsPath);
        BenchmarkRunner.Run<CloudProfileDataSerializationBenchmark>(config);
    }

    private static string GetReportsRoot() => Path.Combine(
        VisualStudioProvider.GetPathToPrerequisites(VisualStudioProvider.TryGetSolutionDirectoryInfo().FullName),
        "NBomber_Reports"
    );

    private static async Task<IResponse> Execute(IScenarioContext context)
    {
        var client = _clients.GetOrAdd(context.ScenarioInfo.InstanceId, static _ =>
        {
            var c = new CacheServiceTcpClient(_host, _port);
            c.ConnectAsync().GetAwaiter().GetResult();
            return c;
        });

        return await Step.Run("client_step", context, async () =>
        {
            var profile = CloudProfileDataGenerator.Generate();

            try
            {
                // SetAsync -> CacheServiceResponse; GetAsync<T> -> CacheServiceResponse<T>.
                // Разные типы после обобщения интерфейса — вытаскиваем ResponseCode отдельно.
                CacheServiceResponseCode code;
                if (Random.Shared.Next() % 2 == 0)
                    code = (await client.SetAsync(profile.SrcId!, profile)).ResponseCode;
                else
                    code = (await client.GetAsync<CloudProfileData>(profile.SrcId!)).ResponseCode;

                return code switch
                {
                    CacheServiceResponseCode.Ok => Response.Ok(),
                    CacheServiceResponseCode.Nil => Response.Ok(),
                    _ => Response.Fail(),
                };
            }
            catch
            {
                return Response.Fail();
            }
        });
    }

    private static async Task<IResponse> ExecuteWithPooledClient(IScenarioContext context)
    {
        var client = _pooledClient!;

        return await Step.Run("client_step", context, async () =>
        {
            var profile = CloudProfileDataGenerator.Generate();

            try
            {
                // SetAsync -> CacheServiceResponse; GetAsync<T> -> CacheServiceResponse<T>.
                // Разные типы после обобщения интерфейса — вытаскиваем ResponseCode отдельно.
                CacheServiceResponseCode code;
                if (Random.Shared.Next() % 2 == 0)
                    code = (await client.SetAsync(profile.SrcId!, profile)).ResponseCode;
                else
                    code = (await client.GetAsync<CloudProfileData>(profile.SrcId!)).ResponseCode;

                return code switch
                {
                    CacheServiceResponseCode.Ok => Response.Ok(),
                    CacheServiceResponseCode.Nil => Response.Ok(),
                    _ => Response.Fail(),
                };
            }
            catch
            {
                return Response.Fail();
            }
        });
    }

    private static void InitPooledClient(int poolSize)
    {
        _pooledClient = new CacheServiceTcpClient(_host, _port, poolSize: poolSize);
        _pooledClient.ConnectAsync().GetAwaiter().GetResult();
    }

    private static void DisposePooledClient()
    {
        _pooledClient?.Dispose();
        _pooledClient = null;
    }

    private static void DisposeClients()
    {
        foreach (var c in _clients.Values)
            c.Dispose();
        _clients.Clear();
    }
}
