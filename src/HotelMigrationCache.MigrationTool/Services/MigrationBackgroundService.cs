using HotelMigrationCache.MigrationTool.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services;

public sealed class MigrationBackgroundService : BackgroundService
{
    private readonly ICacheService _cache;
    private readonly ICloudApiClient _cloud;
    private readonly IMigrationRepository _repo;
    private readonly IRelationshipQueue _relationships;
    private readonly IMigrationTool _tool;
    private readonly IMigrationStatistics _stats;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MigrationBackgroundService> _logger;

    public MigrationBackgroundService(
        ICacheService cache,
        ICloudApiClient cloud,
        IMigrationRepository repo,
        IRelationshipQueue relationships,
        IMigrationTool tool,
        IMigrationStatistics stats,
        IHostApplicationLifetime lifetime,
        ILogger<MigrationBackgroundService> logger)
    {
        _cache = cache;
        _cloud = cloud;
        _repo = repo;
        _relationships = relationships;
        _tool = tool;
        _stats = stats;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("Starting migration background service...");

            await _repo.InitializeSchemaAsync(stoppingToken);
            await _relationships.InitializeSchemaAsync(stoppingToken);
            await _cloud.InitializeAsync(stoppingToken);
            await _cache.StartAsync(stoppingToken);

            _stats.StartOverall();
            try { await _tool.MigrateAsync(stoppingToken); }
            finally { _stats.StopOverall(); }

            _logger.LogInformation("Migration completed.");
        }
        catch (OperationCanceledException ocex)
        {
            _logger.LogWarning(ocex, "Migration was stopped by request.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migration failed.");
            throw;
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}
