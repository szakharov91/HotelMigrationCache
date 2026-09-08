using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.MigrationTool.Contracts;

// Кэш справочных данных отеля.
// В диплом-варианте бэкенд — TCP-сервис кэша (тот же, что и для profile-cache).
// Уникальных ключей мало (~5-15) → hit rate стремится к 100% уже после первых lookup'ов.
// Констрейнт `IBinarySerializable<T>` — потому что значение улетает в TCP-кэш и обратно
// через нашу source-gen сериализацию.
public interface IReferenceCache
{
    Task<T> GetOrLoadAsync<T>(string key, Func<CancellationToken, Task<T>> loader, CancellationToken ct)
        where T : IBinarySerializable<T>;
}
