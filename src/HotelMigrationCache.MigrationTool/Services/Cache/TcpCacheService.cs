using System.Collections.Concurrent;
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
/// уровень gate не нужен.
/// <para>
/// Инстанция ведёт in-process множество всех ключей, к которым обращались (SET / GET / DELETE).
/// Множество используется в <see cref="DeleteAllTrackedKeysAsync"/> для чистки кэша между
/// сценариями `--compare3` — чтобы Run 3 стартовал с того же холодного состояния, что и Run 2.
/// </para></summary>
public sealed class TcpCacheService : ICacheService, IDisposable
{
    private readonly ICacheServiceClient _client;
    private readonly IMigrationStatistics _stats;
    private readonly ILogger<TcpCacheService> _logger;
    // ConcurrentDictionary как thread-safe set (значение не используется).
    private readonly ConcurrentDictionary<string, byte> _trackedKeys = new(StringComparer.Ordinal);

    public TcpCacheService(
        CacheServiceOptions cacheOptions,
        MigrationOptions migrationOptions,
        IMigrationStatistics stats,
        ILogger<TcpCacheService> logger)
    {
        var poolSize = (int)migrationOptions.Concurrency;
        _client = new CacheServiceTcpClient(cacheOptions.Host, cacheOptions.Port, poolSize);
        _stats = stats;
        _logger = logger;
        logger.LogInformation("Init {ServiceName} → {Host}:{Port} · pool size {PoolSize}",
            nameof(TcpCacheService), cacheOptions.Host, cacheOptions.Port, poolSize);
    }

    public Task StartAsync(CancellationToken ct) => _client.ConnectAsync();

    public async Task<CacheServiceResponse<TValue>> GetAsync<TValue>(string key)
        where TValue : IBinarySerializable<TValue>
    {
        using var _ = _stats.TrackCacheOperation();
        _trackedKeys.TryAdd(key, 0);
        return await _client.GetAsync<TValue>(key);
    }

    public async Task<CacheServiceResponse> SetAsync<TValue>(string key, TValue value)
        where TValue : IBinarySerializable<TValue>
    {
        using var _ = _stats.TrackCacheOperation();
        _trackedKeys.TryAdd(key, 0);
        return await _client.SetAsync(key, value);
    }

    public async Task<CacheServiceResponse> DeleteAsync(string key)
    {
        using var _ = _stats.TrackCacheOperation();
        _trackedKeys.TryAdd(key, 0);
        return await _client.DeleteAsync(key);
    }

    public async Task<CacheStatistics?> GetServerStatisticsAsync()
    {
        using var _ = _stats.TrackCacheOperation();
        return await _client.GetStatisticsAsync();
    }

    public async Task<int> DeleteAllTrackedKeysAsync()
    {
        // Копируем ключи в массив, чтобы не держать enumerator на живой коллекции.
        var keys = _trackedKeys.Keys.ToArray();
        if (keys.Length == 0)
        {
            _logger.LogInformation("Cache cleanup: no tracked keys to delete.");
            return 0;
        }

        int deletedCount = 0;
        int errorCount = 0;

        foreach (var key in keys)
        {
            try
            {
                // Не оборачиваем в _stats.TrackCacheOperation — это maintenance,
                // не должен искажать метрики миграции. Метрики уже сняты в snapshot до этого шага.
                var resp = await _client.DeleteAsync(key);
                if (resp.ResponseCode is CacheServiceResponseCode.Ok or CacheServiceResponseCode.Nil)
                    deletedCount++;
                else
                    errorCount++;
            }
            catch (Exception ex)
            {
                errorCount++;
                _logger.LogWarning(ex, "Cache cleanup: DELETE failed for key {Key}", key);
            }
        }

        _trackedKeys.Clear();

        _logger.LogInformation(
            "Cache cleanup: {Deleted}/{Total} keys removed via DELETE ({Errors} errors)",
            deletedCount, keys.Length, errorCount);

        return deletedCount;
    }

    public void Dispose() => _client.Dispose();
}
