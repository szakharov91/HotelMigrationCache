namespace HotelMigrationCache.MigrationTool.Contracts;

public interface IMigrationStatistics
{
    void StartOverall();
    void StopOverall();

    IDisposable TrackProfileMigration();
    IDisposable TrackReservationMigration();

    void IncrementProfileSuccess();
    void IncrementProfileFailure();
    void IncrementReservationSuccess();
    void IncrementReservationFailure();

    // Кэш-события (в фазе миграции броней): попадание, промах, реальный уход в облако.
    void IncrementCacheHit();
    void IncrementCacheMiss();
    void IncrementCloudFallback();

    // Кэш справочных данных отеля (RoomType/RateCode/LoyaltyRule/PaymentType/Preference).
    void IncrementReferenceCacheHit();
    void IncrementReferenceCacheMiss();

    // Трекеры длительности обращений к разным backend-ам.
    // Используются как: using var _ = _stats.TrackCloudOperation();  await ...
    IDisposable TrackCacheOperation();
    IDisposable TrackLocalDbOperation();
    IDisposable TrackCloudOperation();

    MigrationStatisticsSnapshot Snapshot();
}

public sealed record MigrationStatisticsSnapshot(
    TimeSpan Overall,
    int ProfilesTotal,
    int ProfilesSucceeded,
    int ProfilesFailed,
    TimeSpan ProfileMin,
    TimeSpan ProfileMax,
    TimeSpan ProfileAverage,
    TimeSpan ProfileTotal,
    int ReservationsTotal,
    int ReservationsSucceeded,
    int ReservationsFailed,
    TimeSpan ReservationMin,
    TimeSpan ReservationMax,
    TimeSpan ReservationAverage,
    TimeSpan ReservationTotal,
    int CacheHits,
    int CacheMisses,
    int CloudFallbacks,
    TimeSpan AvgCloudLatency,
    int CacheOperations,
    TimeSpan CacheOperationsTime,
    int LocalDbOperations,
    TimeSpan LocalDbOperationsTime,
    int CloudOperations,
    TimeSpan CloudOperationsTime,
    int ReferenceCacheHits,
    int ReferenceCacheMisses,
    int MigrationParallelism,
    decimal CostPer10kCalls,
    decimal ThrottlingOverheadFactor,
    int ProjectedRecordCount)
{
    public double CacheHitRate => CacheHits + CacheMisses == 0
        ? 0.0
        : (double)CacheHits / (CacheHits + CacheMisses);

    public int CloudCallsSaved => CacheHits;

    public TimeSpan EstimatedTimeSaved => TimeSpan.FromTicks(AvgCloudLatency.Ticks * CacheHits);

    public double ReferenceCacheHitRate => ReferenceCacheHits + ReferenceCacheMisses == 0
        ? 0.0
        : (double)ReferenceCacheHits / (ReferenceCacheHits + ReferenceCacheMisses);

    public int ReferenceCloudCallsSaved => ReferenceCacheHits;

    public TimeSpan ReferenceEstimatedTimeSaved => TimeSpan.FromTicks(AvgCloudLatency.Ticks * ReferenceCacheHits);

    // Приближённое wall-clock из serial-equivalent метрик: total / parallelism.
    // Точно для параллелящихся операций (cloud/db), для сериализованных (Cache со SemaphoreSlim(1,1))
    // wall-clock ≈ total — но там значения микроскопические, поэтому приближение приемлемо.
    private int Divisor => Math.Max(1, MigrationParallelism);
    public TimeSpan ProfileTotalWallClock => TimeSpan.FromTicks(ProfileTotal.Ticks / Divisor);
    public TimeSpan ReservationTotalWallClock => TimeSpan.FromTicks(ReservationTotal.Ticks / Divisor);
    public TimeSpan CloudOperationsWallClock => TimeSpan.FromTicks(CloudOperationsTime.Ticks / Divisor);
    public TimeSpan LocalDbOperationsWallClock => TimeSpan.FromTicks(LocalDbOperationsTime.Ticks / Divisor);
    public TimeSpan EstimatedWallClockSaved => TimeSpan.FromTicks(EstimatedTimeSaved.Ticks / Divisor);
    public TimeSpan ReferenceEstimatedWallClockSaved => TimeSpan.FromTicks(ReferenceEstimatedTimeSaved.Ticks / Divisor);

    // --- Cloud API cost accounting ---

    private decimal CostPerCall => CostPer10kCalls / 10_000m;
    public decimal EstimatedCloudCost => CloudOperations * CostPerCall;
    public decimal EstimatedCloudCostWithThrottling => EstimatedCloudCost * ThrottlingOverheadFactor;

    public int TotalProcessedRecords => ProfilesTotal + ReservationsTotal;
    public decimal AvgCostPerRecord =>
        TotalProcessedRecords == 0 ? 0m : EstimatedCloudCost / TotalProcessedRecords;

    // Проекция: если бы выполнили ProjectedRecordCount записей с той же удельной стоимостью.
    public decimal ProjectedCost =>
        TotalProcessedRecords == 0 ? 0m : AvgCostPerRecord * ProjectedRecordCount;
    public decimal ProjectedCostWithThrottling => ProjectedCost * ThrottlingOverheadFactor;
}
