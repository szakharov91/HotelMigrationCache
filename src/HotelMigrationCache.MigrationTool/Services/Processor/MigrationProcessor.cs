using System.Runtime.CompilerServices;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using HotelMigrationCache.MigrationTool.Options;
using HotelMigrationCache.MigrationTool.Services.Loader;
using HotelMigrationCache.MigrationTool.Services.Statistics;
using HotelMigrationCache.MigrationTool.Ui;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.MigrationTool.Services.Processor;

public sealed class MigrationProcessor : IMigrationProcessor
{
    private const int _initBatchSize = 500;

    private readonly ISourceFilesLoader _loader;
    private readonly IMigrationRepository _repo;
    private readonly IProfileMigrator _profileMigrator;
    private readonly IReservationMigrator _reservationMigrator;
    private readonly IRelationshipQueue _relationshipQueue;
    private readonly ICloudApiClient _cloud;
    private readonly ICacheService _cache;
    private readonly IReferenceCache _refCache;
    private readonly IMigrationStatistics _stats;
    private readonly CacheTelemetryCollector _telemetry;
    private readonly JaegerTraceReader _jaeger;
    private readonly IMigrationUi _ui;
    private readonly MigrationOptions _options;

    public MigrationProcessor(
        ISourceFilesLoader loader,
        IMigrationRepository repo,
        IProfileMigrator profileMigrator,
        IReservationMigrator reservationMigrator,
        IRelationshipQueue relationshipQueue,
        ICloudApiClient cloud,
        ICacheService cache,
        IReferenceCache refCache,
        IMigrationStatistics stats,
        CacheTelemetryCollector telemetry,
        JaegerTraceReader jaeger,
        IMigrationUi ui,
        MigrationOptions options)
    {
        _loader = loader;
        _repo = repo;
        _profileMigrator = profileMigrator;
        _reservationMigrator = reservationMigrator;
        _relationshipQueue = relationshipQueue;
        _cloud = cloud;
        _cache = cache;
        _refCache = refCache;
        _stats = stats;
        _telemetry = telemetry;
        _jaeger = jaeger;
        _ui = ui;
        _options = options;
    }

    public async Task InitSourceFilesAsync(CancellationToken ct)
    {
        _ui.WriteHeader("Step 1 · Init source files",
            $"profiles: {_options.SourceProfilesDirectory}   bookings: {_options.SourceBookingsDirectory}");

        var profileFiles = CountFiles(_options.SourceProfilesDirectory);
        var bookingFiles = CountFiles(_options.SourceBookingsDirectory);

        var profilesInserted = await _ui.WithProgressAsync(
            "Loading profiles",
            profileFiles,
            async progress =>
            {
                int inserted = 0;
                var source = _loader.LoadProfilesAsync(_options.SourceProfilesDirectory, ct);
                await foreach (var batch in BatchAsync(source, _initBatchSize, ct))
                {
                    var records = batch.Select(p => new ProfileRecord
                    {
                        SourceFilename = p.SourceFileName,
                        FileRawContent = p.RawXmlContent,
                        SrcId = p.ProfileId,
                        State = MigrationState.New,
                    }).ToList();

                    await _repo.InsertProfilesIfMissingAsync(records, ct);
                    inserted += records.Count;
                    progress.Advance(records.Count);
                }
                return inserted;
            },
            ct);

        var reservationsInserted = await _ui.WithProgressAsync(
            "Loading reservations",
            bookingFiles,
            async progress =>
            {
                int inserted = 0;
                var source = _loader.LoadReservationsAsync(_options.SourceBookingsDirectory, ct);
                await foreach (var batch in BatchAsync(source, _initBatchSize, ct))
                {
                    var records = batch.Select(r => new ReservationRecord
                    {
                        SourceFilename = r.SourceFileName,
                        FileRawContent = r.RawXmlContent,
                        SrcId = r.BookingId,
                        MainProfileSrcId = r.MainProfileId,
                        State = MigrationState.New,
                    }).ToList();

                    await _repo.InsertReservationsIfMissingAsync(records, ct);
                    inserted += records.Count;
                    progress.Advance(records.Count);
                }
                return inserted;
            },
            ct);

        var profileCounters = await _repo.GetProfileCountersAsync(ct);
        var reservationCounters = await _repo.GetReservationCountersAsync(ct);

        _ui.WriteInfo($"Profiles     — files: {profileFiles}, loaded: {profilesInserted}, in DB: {profileCounters.Total} ({profileCounters.New} new / {profileCounters.Success} success / {profileCounters.Failed} failed)");
        _ui.WriteInfo($"Reservations — files: {bookingFiles}, loaded: {reservationsInserted}, in DB: {reservationCounters.Total} ({reservationCounters.New} new / {reservationCounters.Success} success / {reservationCounters.Failed} failed)");
    }

    public async Task WarmReferenceCacheAsync(CancellationToken ct)
    {
        _ui.WriteHeader("Step 2 · Warm reference cache", "collect unique refs from source, load in parallel");

        // Собираем уникальные значения из локальной БД (FileRawContent уже загружен на шаге Init).
        var uniqueRoomTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniqueRateCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uniquePreferences = new HashSet<(string cat, string code)>();
        var uniqueCurrencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var p in _repo.GetPendingProfilesAsync(ct))
        {
            try
            {
                var src = SourceXmlParser.ParseProfile(p.FileRawContent, p.SourceFilename);
                if (!string.IsNullOrEmpty(src.Preferences?.Language)) uniquePreferences.Add(("lang", src.Preferences.Language));
                if (!string.IsNullOrEmpty(src.Preferences?.Smoking))  uniquePreferences.Add(("smoke", src.Preferences.Smoking));
                if (!string.IsNullOrEmpty(src.Preferences?.BedType))  uniquePreferences.Add(("bed", src.Preferences.BedType));
                foreach (var r in src.NegotiatedRates) uniqueRateCodes.Add(r.RateCode);
            }
            catch { /* битые файлы пропускаем; они позже отвалятся на миграции */ }
        }

        await foreach (var r in _repo.GetPendingReservationsAsync(ct))
        {
            try
            {
                var src = SourceXmlParser.ParseReservation(r.FileRawContent, r.SourceFilename);
                uniqueRoomTypes.Add(src.Room.RoomType);
                if (!string.IsNullOrEmpty(src.TotalPrice.Currency)) uniqueCurrencies.Add(src.TotalPrice.Currency);
                if (!string.IsNullOrEmpty(src.DepositPaid?.Currency)) uniqueCurrencies.Add(src.DepositPaid.Currency);
            }
            catch { /* пропускаем */ }
        }

        // Статические справочники — знаем без сканирования.
        var loyaltyLevels = Enum.GetValues<LoyaltyLevel>()
            .Where(l => l != LoyaltyLevel.None)
            .ToArray();

        var total = loyaltyLevels.Length + uniqueCurrencies.Count + uniqueRoomTypes.Count
                    + uniqueRateCodes.Count + uniquePreferences.Count;

        _ui.WriteInfo($"Unique refs collected: {loyaltyLevels.Length} loyalty · {uniqueCurrencies.Count} currencies · "
                      + $"{uniqueRoomTypes.Count} room types · {uniqueRateCodes.Count} rate codes · "
                      + $"{uniquePreferences.Count} preferences = {total} total");

        if (total == 0)
        {
            _ui.WriteInfo("Nothing to warm.");
            return;
        }

        // Загружаем всё параллельно — cache miss'ы вылетают одним залпом, а не размазаны по миграции.
        await _ui.WithProgressAsync("Warming reference cache", total, async progress =>
        {
            var tasks = new List<Task>(total);
            foreach (var l in loyaltyLevels)
                tasks.Add(WarmOneAsync($"loy:{l}", tok => _cloud.GetLoyaltyRateRuleAsync(l, tok), progress, ct));
            foreach (var cur in uniqueCurrencies)
                tasks.Add(WarmOneAsync($"pay:{cur}", tok => _cloud.GetPaymentTypeInfoAsync(cur, tok), progress, ct));
            foreach (var rt in uniqueRoomTypes)
                tasks.Add(WarmOneAsync($"room:{rt}", tok => _cloud.GetRoomTypeInfoAsync(rt, tok), progress, ct));
            foreach (var rc in uniqueRateCodes)
                tasks.Add(WarmOneAsync($"rate:{rc}", tok => _cloud.GetRateCodeInfoAsync(rc, tok), progress, ct));
            foreach (var (cat, code) in uniquePreferences)
                tasks.Add(WarmOneAsync($"pref:{cat}:{code}", tok => _cloud.GetPreferenceMappingAsync(cat, code, tok), progress, ct));

            await Task.WhenAll(tasks);
            return 0;
        }, ct);
    }

    private async Task WarmOneAsync<T>(string key, Func<CancellationToken, Task<T>> loader, IUiProgress progress, CancellationToken ct)
        where T : IBinarySerializable<T>
    {
        try { await _refCache.GetOrLoadAsync(key, loader, ct); }
        finally { progress.Advance(); }
    }

    public async Task MigrateProfilesAsync(CancellationToken ct)
    {
        var limit = _options.MaxProfilesToMigrate;
        _ui.WriteHeader("Step 3 · Migrate profiles",
            limit > 0 ? $"limit: {limit}, parallelism: {(int)_options.Concurrency}"
                      : $"no limit, parallelism: {(int)_options.Concurrency}");

        var records = await CollectPendingAsync(_repo.GetPendingProfilesAsync(ct), limit, ct);
        if (records.Count == 0)
        {
            _ui.WriteInfo("Nothing to migrate.");
            return;
        }

        await _ui.WithProgressAsync("Migrating profiles", records.Count, async progress =>
        {
            await Parallel.ForEachAsync(
                records,
                new ParallelOptions { MaxDegreeOfParallelism = (int)_options.Concurrency, CancellationToken = ct },
                async (rec, token) =>
                {
                    await _repo.UpdateProfileStateAsync(rec.SrcId, MigrationState.Processing, null, null, token);

                    string? dstId;
                    using (_stats.TrackProfileMigration())
                    {
                        dstId = await _profileMigrator.MigrateAsync(rec, token);
                    }

                    if (dstId is not null)
                    {
                        await _repo.UpdateProfileStateAsync(rec.SrcId, MigrationState.Success, dstId, null, token);
                        _stats.IncrementProfileSuccess();
                    }
                    else
                    {
                        await _repo.UpdateProfileStateAsync(rec.SrcId, MigrationState.Failed, null, "migration returned null", token);
                        _stats.IncrementProfileFailure();
                    }

                    progress.Advance();
                });
            return 0;
        }, ct);
    }

    public async Task MigrateRelationshipsAsync(CancellationToken ct)
    {
        var pending = await _relationshipQueue.GetPendingCountAsync(ct);
        _ui.WriteHeader("Step 4 · Migrate relationships",
            $"pending: {pending}, parallelism: {(int)_options.Concurrency}");

        if (pending == 0)
        {
            _ui.WriteInfo("No relationships to process.");
            return;
        }

        var items = new List<PendingRelationship>();
        await foreach (var item in _relationshipQueue.DrainPendingAsync(ct))
            items.Add(item);

        await _ui.WithProgressAsync("Migrating relationships", items.Count, async progress =>
        {
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = (int)_options.Concurrency, CancellationToken = ct },
                async (rel, token) =>
                {
                    try
                    {
                        var src = await ResolveGuestDstIdAsync(rel.SourceSrcId, token);
                        var tgt = await ResolveGuestDstIdAsync(rel.TargetSrcId, token);

                        if (src is null || tgt is null)
                        {
                            await _relationshipQueue.MarkProcessedAsync(rel.Id, success: false,
                                error: $"unresolved src={src is not null} tgt={tgt is not null}", token);
                            return;
                        }

                        await _cloud.AddProfileRelationshipAsync(new CloudProfileRelationshipRequest(
                            src, tgt, rel.RelationType), token);

                        await _relationshipQueue.MarkProcessedAsync(rel.Id, success: true, error: null, token);
                    }
                    catch (Exception ex)
                    {
                        await _relationshipQueue.MarkProcessedAsync(rel.Id, success: false, error: ex.Message, token);
                    }
                    finally
                    {
                        progress.Advance();
                    }
                });
            return 0;
        }, ct);
    }

    // Тот же fallback-cascade, но нужен здесь только для получения DstId (без loyalty).
    private async Task<string?> ResolveGuestDstIdAsync(string srcId, CancellationToken ct)
    {
        try
        {
            var cached = await _cache.GetAsync<CloudProfileData>(srcId);
            if (cached.ResponseCode == CacheServiceResponseCode.Ok && cached.Value is not null)
            {
                _stats.IncrementCacheHit();
                return cached.Value.DstId;
            }
        }
        catch { /* fall through */ }

        _stats.IncrementCacheMiss();

        var local = await _repo.GetProfileBySrcIdAsync(srcId, ct);
        return string.IsNullOrEmpty(local?.DstId) ? null : local.DstId;
    }

    public async Task MigrateReservationsAsync(CancellationToken ct)
    {
        var limit = _options.MaxReservationsToMigrate;
        _ui.WriteHeader("Step 5 · Migrate reservations",
            limit > 0 ? $"limit: {limit}, parallelism: {(int)_options.Concurrency}"
                      : $"no limit, parallelism: {(int)_options.Concurrency}");

        var records = await CollectPendingAsync(_repo.GetPendingReservationsAsync(ct), limit, ct);
        if (records.Count == 0)
        {
            _ui.WriteInfo("Nothing to migrate.");
            return;
        }

        await _ui.WithProgressAsync("Migrating reservations", records.Count, async progress =>
        {
            await Parallel.ForEachAsync(
                records,
                new ParallelOptions { MaxDegreeOfParallelism = (int)_options.Concurrency, CancellationToken = ct },
                async (rec, token) =>
                {
                    await _repo.UpdateReservationStateAsync(rec.SrcId, MigrationState.Processing, null, null, null, token);

                    string? dstId;
                    using (_stats.TrackReservationMigration())
                    {
                        dstId = await _reservationMigrator.MigrateAsync(rec, token);
                    }

                    if (dstId is not null)
                    {
                        await _repo.UpdateReservationStateAsync(rec.SrcId, MigrationState.Success, dstId, rec.MainProfileDstId, null, token);
                        _stats.IncrementReservationSuccess();
                    }
                    else
                    {
                        await _repo.UpdateReservationStateAsync(rec.SrcId, MigrationState.Failed, null, rec.MainProfileDstId, "migration returned null", token);
                        _stats.IncrementReservationFailure();
                    }

                    progress.Advance();
                });
            return 0;
        }, ct);
    }

    public async Task SummarizeStatisticsAsync(CancellationToken ct)
    {
        _ui.WriteHeader("Step 6 · Summarize",
            _options.UseCacheService ? "run with cache" : "run without cache");
        var snap = _stats.Snapshot();
        _ui.RenderStatisticsTable(snap, _options.UseCacheService ? "Migration stats · with cache" : "Migration stats · no cache");

        // Реальная статистика от ядра кэша (STATS command). Dummy вернёт null.
        try
        {
            var serverStats = await _cache.GetServerStatisticsAsync();
            if (serverStats.HasValue)
                _ui.RenderCacheServerStats(serverStats.Value);
        }
        catch (Exception ex)
        {
            // Соединение с сервером могло быть уже закрыто (compare cleanup). Не критично для отчёта.
            _ = ex;
        }

        // OpenTelemetry-агрегация — работает только если сервер был in-process (наш --compare).
        _ui.RenderCacheTelemetry(_telemetry.Snapshot());

        // Jaeger: если сервер во внешнем процессе (Demo-orchestrator), telemetry ушла туда через OTLP.
        // Пытаемся забрать её обратно через HTTP API. Если Jaeger недоступен — тихий пропуск.
        var jaegerData = await _jaeger.FetchAsync(ct);
        if (jaegerData is not null)
            _ui.RenderJaegerTelemetry(jaegerData);
    }

    private static async Task<List<T>> CollectPendingAsync<T>(IAsyncEnumerable<T> source, int limit, CancellationToken ct)
    {
        var list = new List<T>(limit > 0 ? limit : 128);
        await foreach (var item in source.WithCancellation(ct))
        {
            list.Add(item);
            if (limit > 0 && list.Count >= limit) break;
        }
        return list;
    }

    private static int CountFiles(string dir)
        => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.xml").Count() : 0;

    private static async IAsyncEnumerable<List<T>> BatchAsync<T>(
        IAsyncEnumerable<T> source,
        int size,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new List<T>(size);
        await foreach (var item in source.WithCancellation(ct))
        {
            buffer.Add(item);
            if (buffer.Count >= size)
            {
                yield return buffer;
                buffer = new List<T>(size);
            }
        }
        if (buffer.Count > 0)
            yield return buffer;
    }
}
