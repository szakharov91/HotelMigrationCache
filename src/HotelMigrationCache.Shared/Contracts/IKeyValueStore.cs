using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Shared.Contracts;

/// <summary>Интерфейс нашего кэша</summary>
public interface IKeyValueStore: IDisposable
{
    void Set(byte[] key, byte[] value);
    bool TryGet(byte[] key, out byte[] value);
    bool Delete(byte[] key);
    CacheStatistics GetStatistics();
}
