using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Core.Store;

/// <summary>Интерфейс нашего кэша</summary>
/// <typeparam name="TValue">гарант, что тип значения реализует интерфейс IBinarySerializable </typeparam>
public interface IKeyValueStore<TValue>: IDisposable
    where TValue: IBinarySerializable<TValue>
{
    void Set(string key, TValue value);
    bool TryGet(string key, out TValue value);
    bool Delete(string key);
    CacheStatistics GetStatistics();
}
