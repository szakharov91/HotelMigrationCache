using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.MigrationTool.Services.Cache;

// Пустышка: каждый lookup — miss, всегда идёт loader (== в облако).
// Регистрируется, когда UseCacheService=false — тогда comparison показывает совокупный
// эффект: без кэша каждый reference-lookup платит облачную задержку.
public sealed class NoopReferenceCache : IReferenceCache
{
    private readonly IMigrationStatistics _stats;

    public NoopReferenceCache(IMigrationStatistics stats) => _stats = stats;

    public async Task<T> GetOrLoadAsync<T>(string key, Func<CancellationToken, Task<T>> loader, CancellationToken ct)
        where T : IBinarySerializable<T>
    {
        _stats.IncrementReferenceCacheMiss();
        return await loader(ct);
    }
}
