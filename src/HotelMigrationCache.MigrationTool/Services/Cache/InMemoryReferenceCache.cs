using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.MigrationTool.Services.Cache;

// Reference-cache поверх нашего дипломного TCP-кэша.
// Мотивация переезда с in-process ConcurrentDictionary:
//   1) переживает падение процесса миграции (второй запуск после краша — тёплый reference-set),
//   2) переживает удаление local SQLite (в --compare сценарии между прогонами),
//   3) shared между несколькими инстансами утилиты, если запускать параллельно с разными creds.
//
// Trade-off: hit-path теперь ~230 μs (TCP roundtrip) вместо ~50 нс (dict).
// При 1378 lookup'ах за миграцию это добавляет ~317 мс к wall-clock — незаметно
// на фоне минут работы миграции.
public sealed class InMemoryReferenceCache : IReferenceCache
{
    // Namespace-префикс — чтобы reference-ключи не пересекались с profile-cache ключами (`P-000042` и т.п.)
    private const string _keyPrefix = "ref:";

    private readonly ICacheService _cache;
    private readonly IMigrationStatistics _stats;

    public InMemoryReferenceCache(ICacheService cache, IMigrationStatistics stats)
    {
        _cache = cache;
        _stats = stats;
    }

    public async Task<T> GetOrLoadAsync<T>(string key, Func<CancellationToken, Task<T>> loader, CancellationToken ct)
        where T : IBinarySerializable<T>
    {
        var fullKey = _keyPrefix + key;

        try
        {
            var cached = await _cache.GetAsync<T>(fullKey);
            if (cached.ResponseCode == CacheServiceResponseCode.Ok && cached.Value is not null)
            {
                _stats.IncrementReferenceCacheHit();
                return cached.Value;
            }
        }
        catch
        {
            // Проблемы с транспортом не должны валить миграцию — fallback в облако через loader ниже.
        }

        _stats.IncrementReferenceCacheMiss();
        var value = await loader(ct);

        try { await _cache.SetAsync(fullKey, value); }
        catch { /* прогрев best-effort — при неудаче отдаём значение сразу */ }

        return value;
    }
}
