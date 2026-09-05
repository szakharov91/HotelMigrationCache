using System.Collections.Concurrent;
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

    /// <summary>Постоянное подключение на каждого виртуального пользователя (VU).
    /// Ключ — уникальный InstanceId копии в рамках одной NBomber-сессии. </summary>
    private static readonly ConcurrentDictionary<string, CacheServiceTcpClient> _clients = new();

    /// <summary> Пул для разомкнутой модели: N долгоживущих подключений, каждое запросы через него 
    /// сериализуются семафором (одно соединение = один stream, конкурентно писать нельзя).</summary>
    private static CacheServiceTcpClient[]? _pool;
    private static SemaphoreSlim[]? _poolLocks;
    private static int _rrCounter;

    public static async Task Main(string[] args)
    {
        if (args.Contains("--bench"))
        {
            BenchmarkRunner.Run<CloudProfileDataSerializationBenchmark>();
            return;
        }

        Console.WriteLine("Hello NBomber!");
        Console.WriteLine($"Waiting for server on tcp://{_host}:{_port} ...");
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Сценарий 1: рабочий профиль — 10 постоянных подключений,
        // каждое переиспользует TCP-сессию. Показывает устойчивый RPS
        // при реальной ожидаемой параллельности.
        var sustained = Scenario.Create("sustained_10_conn", Execute)
            .WithLoadSimulations(
                Simulation.KeepConstant(copies: 10, during: TimeSpan.FromSeconds(30))
            )
            .WithWarmUpDuration(TimeSpan.FromSeconds(3));

        // Сценарий 2: пиковая пропускная способность — много параллельных VU,
        // каждый со своим долгоживущим подключением. Ищет верхнюю границу RPS
        // самого кэша, а не сокет-акробатики.
        var stress = Scenario.Create("throughput_stress", Execute)
            .WithLoadSimulations(
                Simulation.KeepConstant(copies: 200, during: TimeSpan.FromSeconds(30))
            )
            .WithWarmUpDuration(TimeSpan.FromSeconds(3));

        // Сценарий 3: разомкнутая модель — фиксированная скорость инъекции
        // (rate rps независимо от того, завершились ли предыдущие запросы),
        // против пула из 10 постоянных подключений (как в реальной эксплуатации).
        // Если кэш не тянет — latency поедет вверх, ok/rps упадут.
        var openRate = Scenario.Create("open_rate_pool_10", ExecuteWithPool)
            .WithLoadSimulations(
                Simulation.Inject(rate: 3000, interval: TimeSpan.FromSeconds(1), during: TimeSpan.FromSeconds(30))
            )
            .WithWarmUpDuration(TimeSpan.FromSeconds(3));

        // Каждый сценарий запускаем отдельной сессией, чтобы отчёты
        // не смешивались и load одного не давил на измерения другого.
        Run(sustained, "sustained");
        DisposeClients();

        Run(stress, "stress");
        DisposeClients();

        InitPool();
        Run(openRate, "open_rate");
        DisposePool();

        Console.WriteLine("Done. Press <Enter> to exit.");
        Console.ReadLine();
    }

    private static void Run(ScenarioProps scenario, string label)
    {
        var reportsRoot = Path.Combine(
            VisualStudioProvider.GetPathToPrerequisites(VisualStudioProvider.TryGetSolutionDirectoryInfo().FullName),
            "NBomber_Reports"
        );

        var runner = NBomberRunner.RegisterScenarios(scenario);
        if (Directory.Exists(reportsRoot))
        {
            runner = runner.WithReportFolder(
                Path.Combine(reportsRoot, $"{DateTime.Now:yyyy_MM_dd-HH_mm_ss}_{label}")
            );
        }

        runner.Run();
    }

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
                var response = Random.Shared.Next() % 2 == 0
                    ? await client.SetAsync(profile.SrcId!, profile)
                    : await client.GetAsync(profile.SrcId!);

                if (response is null)
                    return Response.Fail();

                return response.ResponseCode switch
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

    private static async Task<IResponse> ExecuteWithPool(IScenarioContext context)
    {
        int idx = (int)((uint)Interlocked.Increment(ref _rrCounter) % (uint)_openPoolSize);
        var client = _pool![idx];
        var gate = _poolLocks![idx];

        return await Step.Run("client_step", context, async () =>
        {
            var profile = CloudProfileDataGenerator.Generate();

            try
            {
                await gate.WaitAsync();
                try
                {
                    var response = Random.Shared.Next() % 2 == 0
                        ? await client.SetAsync(profile.SrcId!, profile)
                        : await client.GetAsync(profile.SrcId!);

                    if (response is null)
                        return Response.Fail();

                    return response.ResponseCode switch
                    {
                        CacheServiceResponseCode.Ok => Response.Ok(),
                        CacheServiceResponseCode.Nil => Response.Ok(),
                        _ => Response.Fail(),
                    };
                }
                finally
                {
                    gate.Release();
                }
            }
            catch
            {
                return Response.Fail();
            }
        });
    }

    private static void InitPool()
    {
        _pool = new CacheServiceTcpClient[_openPoolSize];
        _poolLocks = new SemaphoreSlim[_openPoolSize];
        for (int i = 0; i < _openPoolSize; i++)
        {
            var c = new CacheServiceTcpClient(_host, _port);
            c.ConnectAsync().GetAwaiter().GetResult();
            _pool[i] = c;
            _poolLocks[i] = new SemaphoreSlim(1, 1);
        }
    }

    private static void DisposePool()
    {
        if (_pool is null || _poolLocks is null)
            return;

        for (int i = 0; i < _openPoolSize; i++)
        {
            _pool[i].Dispose();
            _poolLocks[i].Dispose();
        }
        _pool = null;
        _poolLocks = null;
    }

    private static void DisposeClients()
    {
        foreach (var c in _clients.Values)
            c.Dispose();
        _clients.Clear();
    }
}
