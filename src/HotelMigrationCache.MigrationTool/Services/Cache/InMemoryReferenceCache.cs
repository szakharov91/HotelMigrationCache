using System.Collections.Concurrent;
using HotelMigrationCache.MigrationTool.Contracts;

namespace HotelMigrationCache.MigrationTool.Services.Cache;

// In-process reference cache. Первый lookup ключа идёт в облако (miss),
// последующие — из памяти (hit). Гонки при параллельной миграции могут вызвать
// пару лишних loader'ов на первый ключ — допустимая цена простоты.
public sealed class InMemoryReferenceCache : IReferenceCache
{
    private readonly ConcurrentDictionary<string, object?> _cache = new(StringComparer.Ordinal);
    private readonly IMigrationStatistics _stats;

    public InMemoryReferenceCache(IMigrationStatistics stats) => _stats = stats;

    public async Task<T> GetOrLoadAsync<T>(string key, Func<CancellationToken, Task<T>> loader, CancellationToken ct)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            _stats.IncrementReferenceCacheHit();
            return (T)cached!;
        }

        _stats.IncrementReferenceCacheMiss();
        var value = await loader(ct);
        _cache.TryAdd(key, value);
        return value;
    }
}
