using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Core.Store;

public interface IKeyValueStore<TValue>
    where TValue: IBinarySerializable<TValue>
{
    void Set(string key, TValue value);
    bool TryGet(string key, out TValue value);
    bool Delete(string key);
    CacheStatistics GetStatistics();
}
