namespace HotelMigrationCache.MigrationTool.Contracts;

// Кэш справочных данных отеля. Работает всегда в процессе (данные read-only).
// Уникальных ключей мало (~5-15) → hit rate стремится к 100% уже после первых lookup'ов.
public interface IReferenceCache
{
    Task<T> GetOrLoadAsync<T>(string key, Func<CancellationToken, Task<T>> loader, CancellationToken ct);
}
