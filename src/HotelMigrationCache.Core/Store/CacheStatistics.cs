namespace HotelMigrationCache.Core.Store;

public readonly record struct CacheStatistics(long Count, long HitCount, long MissCount, long SetCount, long DeleteCount);
