using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.MigrationTool.Contracts;

/// <summary>Абстракция над кэшем: скрывает Dummy vs Tcp, позволяет включать/выключать кэш флагом.
/// Обобщена по типу значения (see <see cref="IBinarySerializable{TSelf}"/>) — сам мигратор
/// пока использует только <c>CloudProfileData</c>, но интерфейс не привязан.</summary>
public interface ICacheService
{
    Task StartAsync(CancellationToken ct);

    Task<CacheServiceResponse<TValue>> GetAsync<TValue>(string key)
        where TValue : IBinarySerializable<TValue>;

    Task<CacheServiceResponse> SetAsync<TValue>(string key, TValue value)
        where TValue : IBinarySerializable<TValue>;

    Task<CacheServiceResponse> DeleteAsync(string key);

    // Реальная статистика от ядра кэша: hits/misses/sets/deletes/count.
    // Null — сервер недоступен (например Dummy, или TCP-соединение потеряно).
    Task<CacheStatistics?> GetServerStatisticsAsync();
}
