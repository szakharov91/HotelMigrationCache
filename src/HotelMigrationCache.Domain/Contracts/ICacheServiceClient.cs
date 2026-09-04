using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Shared.Contracts;

public interface ICacheServiceClient : IDisposable
{
    Task<CacheServiceResponseCode> SetAsync(string key, CloudProfileData value);
    Task<CacheServiceResponseCode> DeleteAsync(string key);
    Task<CloudProfileData> GetAsync(string key);
}
