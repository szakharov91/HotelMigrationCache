namespace HotelMigrationCache.Shared.Common;

public record CacheServiceResponse(CacheServiceResponseCode ResponseCode, CloudProfileData? CloudProfileData = null);
