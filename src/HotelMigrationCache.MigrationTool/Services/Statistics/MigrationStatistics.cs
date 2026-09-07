using System.Diagnostics;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Options;

namespace HotelMigrationCache.MigrationTool.Services.Statistics;

public sealed class MigrationStatistics : IMigrationStatistics
{
    private readonly Stopwatch _overall = new();
    private readonly TimeSpan _avgCloudLatency;
    private readonly int _parallelism;
    private readonly PricingOptions _pricing;

    public MigrationStatistics(CloudApiOptions cloud, MigrationOptions migration, PricingOptions pricing)
    {
        var midMs = (cloud.MinLatencyMs + cloud.MaxLatencyMs) / 2.0;
        _avgCloudLatency = TimeSpan.FromMilliseconds(midMs);
        _parallelism = (int)migration.Concurrency;
        _pricing = pricing;
    }

    private int _profileSuccess, _profileFailure;
    private int _reservationSuccess, _reservationFailure;

    private int _profileTracked;
    private long _profileTotalTicks;
    private long _profileMinTicks = long.MaxValue;
    private long _profileMaxTicks;

    private int _reservationTracked;
    private long _reservationTotalTicks;
    private long _reservationMinTicks = long.MaxValue;
    private long _reservationMaxTicks;

    private int _cacheHits, _cacheMisses, _cloudFallbacks;
    private int _refCacheHits, _refCacheMisses;

    // Backend-timing: cумма длительностей и число операций.
    private int _cacheOps;
    private long _cacheOpsTicks;
    private int _localDbOps;
    private long _localDbOpsTicks;
    private int _cloudOps;
    private long _cloudOpsTicks;

    public void StartOverall() => _overall.Restart();
    public void StopOverall() => _overall.Stop();

    public IDisposable TrackProfileMigration() => new StageTracker(this, StageTracker.Kind.Profile);
    public IDisposable TrackReservationMigration() => new StageTracker(this, StageTracker.Kind.Reservation);

    public IDisposable TrackCacheOperation() => new BackendTracker(this, BackendTracker.Kind.Cache);
    public IDisposable TrackLocalDbOperation() => new BackendTracker(this, BackendTracker.Kind.LocalDb);
    public IDisposable TrackCloudOperation() => new BackendTracker(this, BackendTracker.Kind.Cloud);

    public void IncrementProfileSuccess() => Interlocked.Increment(ref _profileSuccess);
    public void IncrementProfileFailure() => Interlocked.Increment(ref _profileFailure);
    public void IncrementReservationSuccess() => Interlocked.Increment(ref _reservationSuccess);
    public void IncrementReservationFailure() => Interlocked.Increment(ref _reservationFailure);

    public void IncrementCacheHit() => Interlocked.Increment(ref _cacheHits);
    public void IncrementCacheMiss() => Interlocked.Increment(ref _cacheMisses);
    public void IncrementCloudFallback() => Interlocked.Increment(ref _cloudFallbacks);

    public void IncrementReferenceCacheHit() => Interlocked.Increment(ref _refCacheHits);
    public void IncrementReferenceCacheMiss() => Interlocked.Increment(ref _refCacheMisses);

    public MigrationStatisticsSnapshot Snapshot()
    {
        var pTracked = Volatile.Read(ref _profileTracked);
        var pSuccess = Volatile.Read(ref _profileSuccess);
        var pFailure = Volatile.Read(ref _profileFailure);
        var pTotal = TimeSpan.FromTicks(Volatile.Read(ref _profileTotalTicks));
        var pAvg = pTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_profileTotalTicks / pTracked);
        var pMin = pTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_profileMinTicks);
        var pMax = pTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_profileMaxTicks);

        var rTracked = Volatile.Read(ref _reservationTracked);
        var rSuccess = Volatile.Read(ref _reservationSuccess);
        var rFailure = Volatile.Read(ref _reservationFailure);
        var rTotal = TimeSpan.FromTicks(Volatile.Read(ref _reservationTotalTicks));
        var rAvg = rTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_reservationTotalTicks / rTracked);
        var rMin = rTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_reservationMinTicks);
        var rMax = rTracked == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(_reservationMaxTicks);

        return new MigrationStatisticsSnapshot(
            Overall: _overall.Elapsed,
            ProfilesTotal: pSuccess + pFailure,
            ProfilesSucceeded: pSuccess,
            ProfilesFailed: pFailure,
            ProfileMin: pMin,
            ProfileMax: pMax,
            ProfileAverage: pAvg,
            ProfileTotal: pTotal,
            ReservationsTotal: rSuccess + rFailure,
            ReservationsSucceeded: rSuccess,
            ReservationsFailed: rFailure,
            ReservationMin: rMin,
            ReservationMax: rMax,
            ReservationAverage: rAvg,
            ReservationTotal: rTotal,
            CacheHits: Volatile.Read(ref _cacheHits),
            CacheMisses: Volatile.Read(ref _cacheMisses),
            CloudFallbacks: Volatile.Read(ref _cloudFallbacks),
            AvgCloudLatency: _avgCloudLatency,
            CacheOperations: Volatile.Read(ref _cacheOps),
            CacheOperationsTime: TimeSpan.FromTicks(Volatile.Read(ref _cacheOpsTicks)),
            LocalDbOperations: Volatile.Read(ref _localDbOps),
            LocalDbOperationsTime: TimeSpan.FromTicks(Volatile.Read(ref _localDbOpsTicks)),
            CloudOperations: Volatile.Read(ref _cloudOps),
            CloudOperationsTime: TimeSpan.FromTicks(Volatile.Read(ref _cloudOpsTicks)),
            ReferenceCacheHits: Volatile.Read(ref _refCacheHits),
            ReferenceCacheMisses: Volatile.Read(ref _refCacheMisses),
            MigrationParallelism: _parallelism,
            CostPer10kCalls: _pricing.CostPer10kCalls,
            ThrottlingOverheadFactor: _pricing.ThrottlingOverheadFactor,
            ProjectedRecordCount: _pricing.ProjectedRecordCount);
    }

    private void RecordProfileTicks(long ticks)
    {
        Interlocked.Increment(ref _profileTracked);
        Interlocked.Add(ref _profileTotalTicks, ticks);
        AtomicMin(ref _profileMinTicks, ticks);
        AtomicMax(ref _profileMaxTicks, ticks);
    }

    private void RecordReservationTicks(long ticks)
    {
        Interlocked.Increment(ref _reservationTracked);
        Interlocked.Add(ref _reservationTotalTicks, ticks);
        AtomicMin(ref _reservationMinTicks, ticks);
        AtomicMax(ref _reservationMaxTicks, ticks);
    }

    private void RecordCache(long ticks)
    {
        Interlocked.Increment(ref _cacheOps);
        Interlocked.Add(ref _cacheOpsTicks, ticks);
    }

    private void RecordLocalDb(long ticks)
    {
        Interlocked.Increment(ref _localDbOps);
        Interlocked.Add(ref _localDbOpsTicks, ticks);
    }

    private void RecordCloud(long ticks)
    {
        Interlocked.Increment(ref _cloudOps);
        Interlocked.Add(ref _cloudOpsTicks, ticks);
    }

    private static void AtomicMin(ref long location, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref location);
            if (value >= current) return;
        } while (Interlocked.CompareExchange(ref location, value, current) != current);
    }

    private static void AtomicMax(ref long location, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref location);
            if (value <= current) return;
        } while (Interlocked.CompareExchange(ref location, value, current) != current);
    }

    private sealed class StageTracker : IDisposable
    {
        public enum Kind { Profile, Reservation }

        private readonly MigrationStatistics _owner;
        private readonly Kind _kind;
        private readonly long _startTs = Stopwatch.GetTimestamp();

        public StageTracker(MigrationStatistics owner, Kind kind) { _owner = owner; _kind = kind; }

        public void Dispose()
        {
            var ticks = Stopwatch.GetElapsedTime(_startTs).Ticks;
            if (_kind == Kind.Profile) _owner.RecordProfileTicks(ticks);
            else _owner.RecordReservationTicks(ticks);
        }
    }

    private sealed class BackendTracker : IDisposable
    {
        public enum Kind { Cache, LocalDb, Cloud }

        private readonly MigrationStatistics _owner;
        private readonly Kind _kind;
        private readonly long _startTs = Stopwatch.GetTimestamp();

        public BackendTracker(MigrationStatistics owner, Kind kind) { _owner = owner; _kind = kind; }

        public void Dispose()
        {
            var ticks = Stopwatch.GetElapsedTime(_startTs).Ticks;
            switch (_kind)
            {
                case Kind.Cache: _owner.RecordCache(ticks); break;
                case Kind.LocalDb: _owner.RecordLocalDb(ticks); break;
                case Kind.Cloud: _owner.RecordCloud(ticks); break;
            }
        }
    }
}
