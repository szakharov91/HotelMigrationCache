using System.Net;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace HotelMigrationCache.MigrationTool;

public sealed record CacheServiceOptions(string Host, int Port);

public interface IInMemoryCacheService
{
    Task StartAsync();
    Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value);
    Task<CacheServiceResponse> DeleteAsync(string key);
    Task<CacheServiceResponse> GetAsync(string key);
}

public sealed class DummyCacheService : IInMemoryCacheService
{
    private readonly CacheServiceResponse _dummyResponse = new CacheServiceResponse(CacheServiceResponseCode.Nil, null);
    
    public DummyCacheService(ILogger<DummyCacheService> logger) => logger.LogInformation("Init {ServiceName}", nameof(DummyCacheService));

    public async Task StartAsync() => await Task.CompletedTask;
    public async Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value) => await Task.FromResult(_dummyResponse);
    public async Task<CacheServiceResponse> DeleteAsync(string key) => await Task.FromResult(_dummyResponse);
    public async Task<CacheServiceResponse> GetAsync(string key) => await Task.FromResult(_dummyResponse);
}

public sealed class InMemoryCacheService : IInMemoryCacheService
{
    private readonly ICacheServiceClient _client;

    public InMemoryCacheService(CacheServiceOptions options, ILogger<InMemoryCacheService> logger)
    {
        _client = new CacheServiceTcpClient(options.Host, options.Port);
        logger.LogInformation("Init {ServiceName}", nameof(InMemoryCacheService));
    }

    public async Task StartAsync() => await _client.ConnectAsync();
    public async Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value) => await _client.SetAsync(key, value);
    public async Task<CacheServiceResponse> DeleteAsync(string key) => await _client.DeleteAsync(key);
    public async Task<CacheServiceResponse> GetAsync(string key) => await _client.GetAsync(key);
}

public interface IProfileMigrator
{
    Task MigrateProfilesAsync(CancellationToken ct);
}

public interface IReservationMigrator
{
    Task MigrateReservationsAsync(CancellationToken ct);
}

public sealed class ReservationMigrator(IInMemoryCacheService cacheService, ILogger<ReservationMigrator> logger) : IReservationMigrator
{
    private readonly IInMemoryCacheService _cacheService = cacheService;
    private readonly ILogger<ReservationMigrator> _logger = logger;
    public async Task MigrateReservationsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Migrating reservations...");

        // Реализация миграции бронирований

        var srcId = "example-src-id-123456";

        CloudProfileData? cloudProfileData = null;
        var cacheResponse = await _cacheService.GetAsync(srcId);

        if (cacheResponse.ResponseCode != CacheServiceResponseCode.Ok || cacheResponse.CloudProfileData == null)
        {
            _logger.LogWarning("Profile with SrcId {SrcId} not found in cache.", srcId);
            // реализация логики достаем из локальной sqlite базы и кладем в кэш
        }
        else
        {
            cloudProfileData = cacheResponse.CloudProfileData;
        }

        // логируем данные cloudProfileData
        _logger.LogInformation("Retrieved profile data: {@CloudProfileData}", cloudProfileData);

        // процессим бронирование для нужного профайла

        // Заполняем accompanying гостей (так же ищем в кэше по srcId, если нет -> ищем в локальной базе, если нет -> ищем в Cloud REST API)

        await Task.CompletedTask;
    }
}

public sealed class ProfileMigrator(IInMemoryCacheService cacheService, ILogger<ProfileMigrator> logger) : IProfileMigrator
{
    private readonly IInMemoryCacheService _cacheService = cacheService;
    private readonly ILogger<ProfileMigrator> _logger = logger;
    public async Task MigrateProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Migrating profiles...");

        // Реализация миграции профайлов
        var srcId = "example-src-id-123456";

        CloudProfileData? cloudProfileData = null;
        var cacheResponse = await _cacheService.GetAsync(srcId);

        // базовая логика: ищем в кэше по srcId, если нет -> ищем в локальной базе, если нет -> ищем в Cloud REST API)

        if (cacheResponse.ResponseCode != CacheServiceResponseCode.Ok || cacheResponse.CloudProfileData == null)
        {
            _logger.LogWarning("Profile with SrcId {SrcId} not found in cache.", srcId);
            // реализация логики достаем из локальной sqlite базы и кладем в кэш
        }
        else
        {
            cloudProfileData = cacheResponse.CloudProfileData;
        }
        
        // логируем данные cloudProfileData
        _logger.LogInformation("Retrieved profile data: {@CloudProfileData}", cloudProfileData);

        await Task.CompletedTask;
    }
}

public interface IMigrationProcessor
{
    /// <summary>Читаем исходные файлы, сортируем сначала профайлы, затем брони, складываем их в локальную sqlite базу</summary>
    Task InitSourceFilesAsync(string sourceDirectory, CancellationToken ct);

    /// <summary>Мигрируем профайлы</summary>
    Task MigrateProfilesAsync(CancellationToken ct);

    /// <summary>Мигрируем брони</summary>
    Task MigrateReservationsAsync(CancellationToken ct);

    /// <summary>Получаем отчет по миграции</summary>
    Task SummarizeStatisticsAsync(CancellationToken ct);

}

public sealed class BasicMigrationProcessor(IProfileMigrator profileMigrator, IReservationMigrator reservationMigrator, ILogger<BasicMigrationTool> logger) : IMigrationProcessor
{
    private readonly IProfileMigrator _profileMigrator = profileMigrator;
    private readonly IReservationMigrator _reservationMigrator = reservationMigrator;
    private readonly ILogger<BasicMigrationTool> _logger = logger;

    public async Task InitSourceFilesAsync(string sourceDirectory, CancellationToken ct)
    {
        _logger.LogInformation("Initializing source files from directory: {SourceDirectory}", sourceDirectory);

        // Реализация чтения исходных файлов и подготовки данных для миграции
        await Task.CompletedTask;
    }
    public async Task MigrateProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Migrating profiles...");

        // Реализация миграции профайлов
        await _profileMigrator.MigrateProfilesAsync(ct);
    }
    public async Task MigrateReservationsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Migrating reservations...");

        // Реализация миграции бронирований
        await _reservationMigrator.MigrateReservationsAsync(ct);
    }
    public async Task SummarizeStatisticsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Summarizing migration statistics...");

        // Реализация получения отчета по миграции
        await Task.CompletedTask;
    }
}

public interface IMigrationTool
{
    Task MigrateAsync(CancellationToken ct);
}

public sealed class BasicMigrationTool(IMigrationProcessor migrationProcessor) : IMigrationTool
{
    public async Task MigrateAsync(CancellationToken ct)
    {
        await migrationProcessor.InitSourceFilesAsync("source", ct);
        await migrationProcessor.MigrateProfilesAsync(ct);
        await migrationProcessor.MigrateReservationsAsync(ct);
        await migrationProcessor.SummarizeStatisticsAsync(ct);
    }
}

public class MigrationBackgroundService : BackgroundService
{
    private readonly IInMemoryCacheService _cacheService;
    private readonly IMigrationTool _migrationTool;
    private readonly ILogger<MigrationBackgroundService> _logger;

    public MigrationBackgroundService(
        IInMemoryCacheService cacheService,
        IMigrationTool migrationTool,
        ILogger<MigrationBackgroundService> logger)
    {
        _cacheService = cacheService;
        _migrationTool = migrationTool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting background service...");

            // Запускаем кэш
            await _cacheService.StartAsync();

            // Запускаем миграцию
            await _migrationTool.MigrateAsync(stoppingToken);

            _logger.LogInformation("Background service has completed its work.");
        }
        catch (OperationCanceledException ocex)
        {
            _logger.LogWarning(ocex, "Background service was stopped by request.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in background service.");
            throw; // можно перебросить, чтобы хост аварийно завершился
        }
    }
}

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("Welcome to PMS Migration tool!");
        Console.WriteLine("Press 'Q' to gracefully stop the application.");

        bool useCacheService = true;
        var cacheServiceOptions = new CacheServiceOptions(IPAddress.Loopback.ToString(), 3456);

        // Создаем и настраиваем хост приложения
        var host = Host.CreateDefaultBuilder(args)
            
            // Подключаем логирование
            .UseSerilog((context, services, config) => config
                    .WriteTo.Console() // пишем в консоль
                    .WriteTo.File("logs/log-.txt", rollingInterval: RollingInterval.Day))

            // Настройка зависимостей
            .ConfigureServices((context, services) =>
            {
                // Базовые настройки для сервиса кэширования
                services.AddSingleton(cacheServiceOptions);

                // Регистрируем сервисы

                if(useCacheService) services.AddSingleton<IInMemoryCacheService, InMemoryCacheService>();
                else services.AddSingleton<IInMemoryCacheService, DummyCacheService>();

                services.AddSingleton<IProfileMigrator, ProfileMigrator>();
                services.AddSingleton<IReservationMigrator, ReservationMigrator>();
                services.AddSingleton<IMigrationProcessor, BasicMigrationProcessor>();
                
                services.AddSingleton<IMigrationTool, BasicMigrationTool>();

                services.AddHostedService<MigrationBackgroundService>();
            })
            .Build();

        using var cts = new CancellationTokenSource(); // для корректной остановки приложения

        _ = Task.Run(() =>
        {
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true).Key;
                    if (key == ConsoleKey.Q)
                    {
                        Console.WriteLine("\nStop signal received, shutting down gracefully...");
                        cts.Cancel();
                        break;
                    }
                }
                Thread.Sleep(100);
            }
        });

        // Запускаем хост с этим токеном – при его отмене хост остановится
        await host.RunAsync(cts.Token);

        Console.WriteLine("Application has been stopped gracefully.");
    }
}
