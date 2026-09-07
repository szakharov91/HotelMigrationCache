using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.MigrationTool.Contracts;

/// <summary>Абстракция над кэшем: скрывает Dummy vs Tcp, позволяет включать/выключать кэш флагом.</summary>
public interface ICacheService
{
    Task StartAsync(CancellationToken ct);
    Task<CacheServiceResponse> GetAsync(string key);
    Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value);
    Task<CacheServiceResponse> DeleteAsync(string key);

    // Реальная статистика от ядра кэша: hits/misses/sets/deletes/count.
    // Null — сервер недоступен (например Dummy, или TCP-соединение потеряно).
    Task<CacheStatistics?> GetServerStatisticsAsync();
}
