using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using Microsoft.Extensions.Logging;

// Пустышка: методы намеренно идентичны — это часть контракта Dummy.
#pragma warning disable S4144

namespace HotelMigrationCache.MigrationTool.Services.Cache;

// Пустышка: всегда возвращает Nil. Время операций учитывается в статистике (обычно ~0),
// чтобы в отчёте "cache time" был честно ~ноль, а не отсутствовал.
public sealed class DummyCacheService : ICacheService
{
    private static readonly CacheServiceResponse _nil = new(CacheServiceResponseCode.Nil);
    private readonly IMigrationStatistics _stats;

    public DummyCacheService(IMigrationStatistics stats, ILogger<DummyCacheService> logger)
    {
        _stats = stats;
        logger.LogInformation("Init {ServiceName}", nameof(DummyCacheService));
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<CacheServiceResponse<TValue>> GetAsync<TValue>(string key)
        where TValue : IBinarySerializable<TValue>
    {
        using var _ = _stats.TrackCacheOperation();
        return Task.FromResult(new CacheServiceResponse<TValue>(CacheServiceResponseCode.Nil));
    }

    public Task<CacheServiceResponse> SetAsync<TValue>(string key, TValue value)
        where TValue : IBinarySerializable<TValue>
    {
        using var _ = _stats.TrackCacheOperation();
        return Task.FromResult(_nil);
    }

    public Task<CacheServiceResponse> DeleteAsync(string key)
    {
        using var _ = _stats.TrackCacheOperation();
        return Task.FromResult(_nil);
    }

    // Dummy не общается с сервером → нет реальной статистики.
    public Task<CacheStatistics?> GetServerStatisticsAsync() => Task.FromResult<CacheStatistics?>(null);

    // Dummy никогда не касался кэша → нечего чистить.
    public Task<int> DeleteAllTrackedKeysAsync() => Task.FromResult(0);
}
