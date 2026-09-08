using System.Net;
using HotelMigrationCache.Core.Interfaces;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using Microsoft.Data.Sqlite;
using HotelMigrationCache.MigrationTool.Options;
using HotelMigrationCache.MigrationTool.Services;
using HotelMigrationCache.MigrationTool.Services.Cache;
using HotelMigrationCache.MigrationTool.Services.CloudApi;
using HotelMigrationCache.MigrationTool.Services.Loader;
using HotelMigrationCache.MigrationTool.Services.Migrators;
using HotelMigrationCache.MigrationTool.Services.Processor;
using HotelMigrationCache.MigrationTool.Services.Statistics;
using HotelMigrationCache.MigrationTool.Services.Storage;
using HotelMigrationCache.MigrationTool.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Spectre.Console;

namespace HotelMigrationCache.MigrationTool;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var compare = args.Contains("--compare");
        var compare3 = args.Contains("--compare3");
        var useCacheService = !args.Contains("--no-cache");
        // Флаг для запуска из Demo-orchestrator: cache TCP server поднят внешним процессом,
        // не пытаемся стартовать in-process — иначе конфликт по порту.
        var _externalCacheServer = args.Contains("--external-cache-server");
        _ = _externalCacheServer; // resolved by RunCompareAsync-body actually skipping in-process startup

        var solutionRoot = FindSolutionRoot();
        var artifacts = Path.Combine(solutionRoot, "artifacts");
        Directory.CreateDirectory(artifacts);

        var migrationOptions = new MigrationOptions(
            SourceProfilesDirectory: Path.Combine(solutionRoot, "prerequisites", "data_for_migration", "sim-prod", "100-500", "profiles"),
            SourceBookingsDirectory: Path.Combine(solutionRoot, "prerequisites", "data_for_migration", "sim-prod", "100-500", "reservations"),
            MigrationDbPath: Path.Combine(artifacts, "migration.db"),
            UseCacheService: useCacheService,
            // Демо-масштаб: 500 профайлов + 2000 броней. 0 = полный прогон.
            MaxProfilesToMigrate: 0,
            MaxReservationsToMigrate: 0,
            // Concurrency пресеты (см. MigrationConcurrency):
            //   Ten (10)         — консервативно, 12% Oracle 50-rps budget. Дефолт для co-tenant gateway.
            //   TwentyFive (25)  — 32% budget. Разумно, если gateway почти эксклюзивен.
            //   Sixty (60)       — 76% budget. BOOSTED — безопасно с кэшем (миссы разрежены), опасно без.
            Concurrency: MigrationConcurrency.TwentyFive);

        var cacheOptions = new CacheServiceOptions(IPAddress.Loopback.ToString(), 3456);
        var cloudOptions = new CloudApiOptions(
            CloudDbPath: Path.Combine(artifacts, "cloud.db"),
            MinLatencyMs: 200,
            MaxLatencyMs: 3000);
        var pricingOptions = new PricingOptions(
            CostPer10kCalls: 20m,           // $20 / 10 000 calls (стандартный vendor rate)
            ThrottlingOverheadFactor: 1.35m, // 35% retry-каскад при rate-limit'ах Oracle Hospitality
            ProjectedRecordCount: 70_000);  // прогноз: medium-класс отеля (20k профайлов + 50k броней)

        if (compare3)
        {
            await RunCompare3Async(migrationOptions, cacheOptions, cloudOptions, pricingOptions);
            return;
        }

        if (compare)
        {
            await RunCompareAsync(migrationOptions, cacheOptions, cloudOptions, pricingOptions);
            return;
        }

        await RunOnceAsync(migrationOptions, cacheOptions, cloudOptions, pricingOptions);

        Console.WriteLine("Press <Enter> key to exit...");
        Console.ReadLine();
    }

    // Два прогона в одном процессе: сначала без кэша, потом с ним.
    // Между прогонами удаляем БД миграции и облака; для второго прогона поднимаем
    // внутрипроцессный TCP-сервер кэша со свежим InMemoryKeyValueStore — так compare
    // самодостаточен и оба прогона стартуют «с чистого листа».
    private static async Task RunCompareAsync(
        MigrationOptions baseOptions,
        CacheServiceOptions cacheOptions,
        CloudApiOptions cloudOptions,
        PricingOptions pricingOptions)
    {
        AnsiConsole.Write(new Rule("[bold yellow]Compare mode: run 1/2 · NO cache[/]").Centered());
        AnsiConsole.WriteLine();

        DeleteArtifacts(baseOptions.MigrationDbPath, cloudOptions.CloudDbPath);
        var noCache = await RunOnceAsync(baseOptions with { UseCacheService = false }, cacheOptions, cloudOptions, pricingOptions);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]Compare mode: run 2/2 · WITH cache (in-process TCP server)[/]").Centered());
        AnsiConsole.WriteLine();

        DeleteArtifacts(baseOptions.MigrationDbPath, cloudOptions.CloudDbPath);



        await Task.Delay(300); // даём слушателю прогреться

        MigrationStatisticsSnapshot withCache;
        try
        {
            withCache = await RunOnceAsync(baseOptions with { UseCacheService = true }, cacheOptions, cloudOptions, pricingOptions);
        }
        finally
        {
            //d
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]Comparison[/]").Centered());
        var ui = new SpectreConsoleUi();
        ui.RenderComparisonTable(noCache, withCache);

        Console.WriteLine("Press <Enter> key to exit...");
        Console.ReadLine();
    }

    // Три прогона: (1) без кэша @ baseline concurrency, (2) с кэшем @ той же concurrency,
    // (3) с кэшем @ boosted concurrency. Показывает разложение выигрыша:
    //   1→2 = вклад кэша при одинаковом parallelism.
    //   2→3 = вклад безопасного повышения parallelism (возможного благодаря кэшу).
    //   1→3 = суммарный эффект.
    private static async Task RunCompare3Async(
        MigrationOptions baseOptions,
        CacheServiceOptions cacheOptions,
        CloudApiOptions cloudOptions,
        PricingOptions pricingOptions)
    {
        var baselineConcurrency = baseOptions.Concurrency;             // например, TwentyFive
        const MigrationConcurrency boostedConcurrency = MigrationConcurrency.Sixty; // 76% Oracle budget

        AnsiConsole.Write(new Rule($"[bold yellow]Compare3 · run 1/3 · NO cache · p={(int)baselineConcurrency}[/]").Centered());
        AnsiConsole.WriteLine();

        DeleteArtifacts(baseOptions.MigrationDbPath, cloudOptions.CloudDbPath);
        var noCache = await RunOnceAsync(
            baseOptions with { UseCacheService = false, Concurrency = baselineConcurrency },
            cacheOptions, cloudOptions, pricingOptions);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold yellow]Compare3 · run 2/3 · WITH cache · p={(int)baselineConcurrency}[/]").Centered());
        AnsiConsole.WriteLine();

        DeleteArtifacts(baseOptions.MigrationDbPath, cloudOptions.CloudDbPath);
        await Task.Delay(300);
        var cacheStandard = await RunOnceAsync(
            baseOptions with { UseCacheService = true, Concurrency = baselineConcurrency },
            cacheOptions, cloudOptions, pricingOptions);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold yellow]Compare3 · run 3/3 · WITH cache · p={(int)boostedConcurrency} (BOOSTED)[/]").Centered());
        AnsiConsole.WriteLine();

        DeleteArtifacts(baseOptions.MigrationDbPath, cloudOptions.CloudDbPath);
        await Task.Delay(300);
        var cacheBoosted = await RunOnceAsync(
            baseOptions with { UseCacheService = true, Concurrency = boostedConcurrency },
            cacheOptions, cloudOptions, pricingOptions);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[bold yellow]Comparison — 3 scenarios[/]").Centered());
        var ui = new SpectreConsoleUi();
        ui.RenderComparisonTable3(noCache, cacheStandard, cacheBoosted);

        Console.WriteLine("Press <Enter> key to exit...");
        Console.ReadLine();
    }

    private static async Task<MigrationStatisticsSnapshot> RunOnceAsync(
        MigrationOptions migrationOptions,
        CacheServiceOptions cacheOptions,
        CloudApiOptions cloudOptions,
        PricingOptions pricingOptions)
    {
        // Не используем host.RunAsync() — он диспоузит host в finally, и Services становятся недоступны.
        // Разбиваем на Start + WaitForShutdown, снимаем статистику до Dispose.
        var host = BuildHost(migrationOptions, cacheOptions, cloudOptions, pricingOptions);
        try
        {
            await host.StartAsync();
            await host.WaitForShutdownAsync();
            var stats = host.Services.GetRequiredService<IMigrationStatistics>();
            return stats.Snapshot();
        }
        finally
        {
            if (host is IAsyncDisposable ad) await ad.DisposeAsync();
            else host.Dispose();
        }
    }

    private static IHost BuildHost(
        MigrationOptions migrationOptions,
        CacheServiceOptions cacheOptions,
        CloudApiOptions cloudOptions,
        PricingOptions pricingOptions) =>
        Host.CreateDefaultBuilder()
            .UseSerilog((context, services, config) => config
                // Spectre-sink: логи и Progress-бар сосуществуют без разрушения UI.
                .WriteTo.Spectre()
                .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day))
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(migrationOptions);
                services.AddSingleton(cacheOptions);
                services.AddSingleton(cloudOptions);
                services.AddSingleton(pricingOptions);

                if (migrationOptions.UseCacheService)
                {
                    services.AddSingleton<ICacheService, TcpCacheService>();
                    services.AddSingleton<IReferenceCache, InMemoryReferenceCache>();
                }
                else
                {
                    services.AddSingleton<ICacheService, DummyCacheService>();
                    services.AddSingleton<IReferenceCache, NoopReferenceCache>();
                }

                services.AddSingleton<IMigrationUi, SpectreConsoleUi>();
                services.AddSingleton<ISourceFilesLoader, XmlSourceFilesLoader>();
                services.AddSingleton<IMigrationRepository, SqliteMigrationRepository>();
                services.AddSingleton<IRelationshipQueue, SqliteRelationshipQueue>();
                services.AddSingleton<ICloudApiClient, SimulatedCloudApiClient>();
                services.AddSingleton<IMigrationStatistics, MigrationStatistics>();
                services.AddSingleton<CacheTelemetryCollector>();
                services.AddSingleton<JaegerTraceReader>();
                services.AddSingleton<IProfileMigrator, ProfileMigrator>();
                services.AddSingleton<IReservationMigrator, ReservationMigrator>();
                services.AddSingleton<IMigrationProcessor, MigrationProcessor>();
                services.AddSingleton<IMigrationTool, Services.MigrationTool>();

                services.AddHostedService<MigrationBackgroundService>();
            })
            .Build();

    private static void DeleteArtifacts(params string[] paths)
    {
        // Microsoft.Data.Sqlite при Pooling=true держит соединения в пуле после Dispose,
        // и файлы БД остаются заблокированными. Принудительно чистим пул перед удалением.
        SqliteConnection.ClearAllPools();

        foreach (var path in paths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var f = path + suffix;
                if (File.Exists(f))
                {
                    try { File.Delete(f); }
                    catch (IOException ex)
                    {
                        AnsiConsole.MarkupLine($"[red]Could not delete {f}: {Markup.Escape(ex.Message)}[/]");
                    }
                }
            }
        }
    }

    // Поднимаемся вверх, пока не найдём .slnx — путь к prerequisites/ не зависит от bin/Debug/… .
    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.slnx").Length == 0 && dir.GetFiles("*.sln").Length == 0)
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
