using System.Collections.ObjectModel;
using System.Net;
using System.Text;
using BenchmarkDotNet.Running;
using HotelMigrationCache.Benchmarks.Benchmarks;
using HotelMigrationCache.Benchmarks.Utils;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Utils;
using NBomber;
using NBomber.CSharp;

namespace HotelMigrationCache.Benchmarks;
public static class Program
{
    private static readonly RandomOptions _randomOptions = new RandomOptions(0, 1000, 6, 12);
    private static readonly Random _rand = Random.Shared;

    public static async Task Main(string[] args)
    {
        if (args.Contains("--bench"))
        {
            BenchmarkRunner.Run<CloudProfileDataSerializationBenchmark>();
            return;
        }

        Console.WriteLine("Hello, NBomber!");

        await Task.Delay(TimeSpan.FromSeconds(5)); // ждем запуска основного приложения

        var scenario = Scenario.Create("tcp_server_load_test", async context =>
        {
            // Внутри сценария определяем шаг с помощью Step.Run
            var response = await Step.Run("client_step", context, async () =>
            {
                var profile = CloudProfileDataGenerator.Generate();

                using ICacheServiceClient client = new CacheServiceTcpClient(IPAddress.Loopback.ToString(), 3456);

                try
                {
                    var response = _rand.Next(_randomOptions.MinValue, _randomOptions.MaxValue) % 2 == 0
                        ? await client.SetAsync(profile.SrcId!, profile)
                        : await client.GetAsync(profile.SrcId!);

                    if (response is null)
                        return Response.Fail(); // Всегда ошибка

                    // Валидные ответы: OK/(nil)/JSON-объект (для GET, если ключ существует)
                    if (response.ResponseCode == CacheServiceResponseCode.Ok ||
                        response.ResponseCode == CacheServiceResponseCode.Nil)
                        return Response.Ok();

                    // Проверяем, что это валидный UserProfile
                    try
                    {
                        return response.CloudProfileData is not null ? Response.Ok() : Response.Fail();
                    }
                    catch (Exception)
                    {
                        return Response.Fail();
                    }
                }
                catch (Exception)
                {
                    // на случай, если у нас происходит какой-то сетевой сбой
                    return Response.Fail();
                }
            });

            return response;
        })
        .WithLoadSimulations(
            // разогрев (10 р/с, 5 сек)
            Simulation.Inject(10, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)),

            // основная нагрузка
            Simulation.Inject(800, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30))
        );

        string reportsFolder = Path.Combine(
            VisualStudioProvider.GetPathToPrerequisites(VisualStudioProvider.TryGetSolutionDirectoryInfo().FullName),
            "NBomber_Reports"
            );

        var nbomber = NBomberRunner.RegisterScenarios(scenario);

        if (Directory.Exists(reportsFolder))
        {
            nbomber = nbomber.WithReportFolder(Path.Combine(reportsFolder, DateTime.Now.ToString("yyyy_MM_dd-HH_mm_ss")));
        }

        nbomber.Run();

        Console.ReadLine();
    }

    private sealed record RandomOptions(int MinValue, int MaxValue, int KeyLength, int UsernameLength);
}
