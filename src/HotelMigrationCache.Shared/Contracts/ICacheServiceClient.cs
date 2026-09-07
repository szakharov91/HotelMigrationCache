using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Shared.Contracts;

public interface ICacheServiceClient : IDisposable
{
    Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value);
    Task<CacheServiceResponse> DeleteAsync(string key);
    Task<CacheServiceResponse> GetAsync(string key);
    Task<CacheStatistics?> GetStatisticsAsync();
    Task ConnectAsync();
}
