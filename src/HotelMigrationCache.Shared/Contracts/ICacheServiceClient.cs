using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Shared.Contracts;

/// <summary>
/// Клиент кэш-сервиса. Обобщён по типу значения через <see cref="IBinarySerializable{TSelf}"/>:
/// каждый TValue приносит свой compile-time сериализатор (Roslyn source generator),
/// клиент про рефлексию и JSON не знает.
/// </summary>
public interface ICacheServiceClient : IDisposable
{
    Task<CacheServiceResponse> SetAsync<TValue>(string key, TValue value)
        where TValue : IBinarySerializable<TValue>;

    Task<CacheServiceResponse> DeleteAsync(string key);

    Task<CacheServiceResponse<TValue>> GetAsync<TValue>(string key)
        where TValue : IBinarySerializable<TValue>;

    Task<CacheStatistics?> GetStatisticsAsync();
    Task ConnectAsync();
}
