using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Options;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Utils;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Cache;

/// <summary>Обёртка над <see cref="CacheServiceTcpClient"/> с пулом соединений,
/// размер которого совпадает с уровнем параллельности миграции.
/// Клиент сам разбирается со сериализацией write→read внутри каждого соединения — здесь
/// уровень gate не нужен.</summary>
public sealed class TcpCacheService : ICacheService, IDisposable
{
    private readonly ICacheServiceClient _client;
    private readonly IMigrationStatistics _stats;

    public TcpCacheService(
        CacheServiceOptions cacheOptions,
        MigrationOptions migrationOptions,
        IMigrationStatistics stats,
        ILogger<TcpCacheService> logger)
    {
        var poolSize = (int)migrationOptions.Concurrency;
        _client = new CacheServiceTcpClient(cacheOptions.Host, cacheOptions.Port, poolSize);
        _stats = stats;
        logger.LogInformation("Init {ServiceName} → {Host}:{Port} · pool size {PoolSize}",
            nameof(TcpCacheService), cacheOptions.Host, cacheOptions.Port, poolSize);
    }

    public Task StartAsync(CancellationToken ct) => _client.ConnectAsync();

    public async Task<CacheServiceResponse> GetAsync(string key)
    {
        using var _ = _stats.TrackCacheOperation();
        return await _client.GetAsync(key);
    }

    public async Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value)
    {
        using var _ = _stats.TrackCacheOperation();
        return await _client.SetAsync(key, value);
    }

    public async Task<CacheServiceResponse> DeleteAsync(string key)
    {
        using var _ = _stats.TrackCacheOperation();
        return await _client.DeleteAsync(key);
    }

    public async Task<CacheStatistics?> GetServerStatisticsAsync()
    {
        using var _ = _stats.TrackCacheOperation();
        return await _client.GetStatisticsAsync();
    }

    public void Dispose() => _client.Dispose();
}
