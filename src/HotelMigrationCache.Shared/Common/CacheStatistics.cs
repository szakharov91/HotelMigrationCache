namespace HotelMigrationCache.Shared.Common;

public readonly record struct CacheStatistics(long Count, long HitCount, long MissCount, long SetCount, long DeleteCount);
