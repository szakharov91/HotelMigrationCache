using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using HotelMigrationCache.MigrationTool.Services.Loader;
using HotelMigrationCache.Shared.Common;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Migrators;

// Расширенный поток по мотивам оригинального syncReservation:
//   1. Резолв main guest + attached companies + billing company (все через cache→local→cloud)
//   2. CreateReservation
//   3. GetReservationConfirmation (получить confirmation number)
//   4. AttachStayProfiles — по одному вызову на каждую прикреплённую компанию
//   5. UpdateReservation — пакеты + billing company
//   6. AddAccompanyingGuests — по одному вызову на гостя
//   7. UpdateRatesForLoyalty (если у main guest есть уровень)
//   8. CancelReservation (если source status == Cancelled)
//
// Каждый resolve — потенциальный cache hit. При отсутствующем гостя (не мигрирован) шаг пропускается с warning.
public sealed class ReservationMigrator : IReservationMigrator
{
    private readonly ICloudApiClient _cloud;
    private readonly ICacheService _cache;
    private readonly IReferenceCache _refCache;
    private readonly IMigrationRepository _repo;
    private readonly IMigrationStatistics _stats;
    private readonly ILogger<ReservationMigrator> _logger;

    public ReservationMigrator(
        ICloudApiClient cloud,
        ICacheService cache,
        IReferenceCache refCache,
        IMigrationRepository repo,
        IMigrationStatistics stats,
        ILogger<ReservationMigrator> logger)
    {
        _cloud = cloud;
        _cache = cache;
        _refCache = refCache;
        _repo = repo;
        _stats = stats;
        _logger = logger;
    }

    public async Task<string?> MigrateAsync(ReservationRecord record, CancellationToken ct)
    {
        SourceReservation source;
        try
        {
            source = SourceXmlParser.ParseReservation(record.FileRawContent, record.SourceFilename);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse failed for reservation {SrcId}", record.SrcId);
            return null;
        }

        var mainGuest = await ResolveGuestAsync(source.MainProfileId, ct);
        if (mainGuest is null || string.IsNullOrEmpty(mainGuest.DstId))
        {
            _logger.LogWarning("Main guest {SrcId} not resolved for reservation {ResSrc} — skipping",
                source.MainProfileId, source.BookingId);
            return null;
        }
        record.MainProfileDstId = mainGuest.DstId;

        // Валидация room type (reference cache — на 5000 броней ~10 уникальных значений).
        await _refCache.GetOrLoadAsync($"room:{source.Room.RoomType}",
            c => _cloud.GetRoomTypeInfoAsync(source.Room.RoomType, c), ct);

        // Валидация payment type по валюте prepaid (уникальных значений ~1-3).
        if (source.DepositPaid is not null)
            await _refCache.GetOrLoadAsync($"pay:{source.DepositPaid.Currency}",
                c => _cloud.GetPaymentTypeInfoAsync(source.DepositPaid.Currency, c), ct);

        string reservationDstId;
        try
        {
            // 1 — Create
            reservationDstId = await _cloud.CreateReservationAsync(new CloudCreateReservationRequest(
                SrcReservationId: source.BookingId,
                MainGuestDstId: mainGuest.DstId!,
                ArrivalDate: source.CheckIn,
                DepartureDate: source.CheckOut,
                RoomCategory: source.Room.RoomType,
                UnitNumber: source.Room.RoomNumber,
                BedSetup: source.Room.BedConfiguration,
                Adults: source.Guests.AdultCount,
                Children: source.Guests.ChildCount,
                ChildAges: source.Guests.ChildrenAges,
                ReservationStatus: MapStatus(source.Status),
                TotalValue: source.TotalPrice.Value,
                TotalCurrency: source.TotalPrice.Currency,
                PrepaidValue: source.DepositPaid?.Value,
                PrepaidCurrency: source.DepositPaid?.Currency,
                Remarks: source.Remarks),
                ct);

            // 2 — GetConfirmation (получаем confirmation number, привязываем к записи облака)
            _ = await _cloud.GetReservationConfirmationAsync(reservationDstId, ct);

            // 3 — AttachStayProfiles (по одному вызову на прикреплённую компанию)
            foreach (var companySrcId in source.AttachedCompanyProfileIds)
            {
                var company = await ResolveGuestAsync(companySrcId, ct);
                if (company is null || string.IsNullOrEmpty(company.DstId))
                {
                    _logger.LogWarning("Attached company {SrcId} not resolved for {ResSrc} — skipped",
                        companySrcId, source.BookingId);
                    continue;
                }
                await _cloud.AttachStayProfileAsync(new CloudStayProfileAttachment(
                    reservationDstId, company.DstId!, CloudStayProfileRole.Company), ct);
            }

            // 4 — UpdateReservation (billing company + packages из услуг)
            string? billingDstId = null;
            if (!string.IsNullOrEmpty(source.BillingCompanyProfileId))
            {
                var billing = await ResolveGuestAsync(source.BillingCompanyProfileId, ct);
                billingDstId = billing?.DstId;
                if (billingDstId is null)
                    _logger.LogWarning("Billing company {SrcId} not resolved for {ResSrc}",
                        source.BillingCompanyProfileId, source.BookingId);
            }

            var packages = source.Services
                .Where(s => s.Price is not null)
                .Select(s => new CloudReservationPackage(PackageCodeFor(s.ServiceType), s.Price!.Value))
                .ToList();

            if (billingDstId is not null || packages.Count > 0)
            {
                await _cloud.UpdateReservationAsync(new CloudReservationUpdateRequest(
                    reservationDstId, billingDstId, packages), ct);
            }

            // 5 — AddAccompanyingGuests
            foreach (var accompSrcId in source.AccompanyingProfileIds)
            {
                var guest = await ResolveGuestAsync(accompSrcId, ct);
                if (guest is null || string.IsNullOrEmpty(guest.DstId))
                {
                    _logger.LogWarning("Accompanying guest {SrcId} not resolved for {ResSrc} — skipped",
                        accompSrcId, source.BookingId);
                    continue;
                }
                await _cloud.AddAccompanyingGuestAsync(reservationDstId, guest.DstId!, ct);
            }

            // 6 — UpdateRatesForLoyalty. Правило скидки — reference lookup (5 уникальных значений всего).
            if (TryGetLoyaltyLevel(mainGuest, out var level) && level != LoyaltyLevel.None)
            {
                await _refCache.GetOrLoadAsync($"loy:{level}",
                    c => _cloud.GetLoyaltyRateRuleAsync(level, c), ct);
                await _cloud.UpdateRatesForLoyaltyAsync(reservationDstId, level, ct);
            }

            // 7 — CancelReservation, если исходный статус — Cancelled
            if (string.Equals(source.Status.Trim(), "Cancelled", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source.Status.Trim(), "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                await _cloud.CancelReservationAsync(reservationDstId, reason: "Migrated as cancelled from source", ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud call failed for reservation {SrcId}", record.SrcId);
            return null;
        }

        return reservationDstId;
    }

    // Cache-first: HIT — из памяти; MISS — local (за DstId) + cloud (за данными). Прогревает кэш.
    private async Task<CloudProfileData?> ResolveGuestAsync(string srcId, CancellationToken ct)
    {
        try
        {
            var cached = await _cache.GetAsync(srcId);
            if (cached.ResponseCode == CacheServiceResponseCode.Ok && cached.CloudProfileData is not null)
            {
                _stats.IncrementCacheHit();
                return cached.CloudProfileData;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache GET failed for {SrcId} — falling back", srcId);
        }

        _stats.IncrementCacheMiss();

        var local = await _repo.GetProfileBySrcIdAsync(srcId, ct);
        if (local is null || string.IsNullOrEmpty(local.DstId))
        {
            _logger.LogWarning("Guest {SrcId} not migrated (no DstId in local DB)", srcId);
            return null;
        }

        CloudProfileSummary? summary;
        try
        {
            _stats.IncrementCloudFallback();
            summary = await _cloud.GetProfileByDstIdAsync(local.DstId!, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud GET failed for guest {SrcId} / {DstId}", srcId, local.DstId);
            return null;
        }

        if (summary is null)
        {
            _logger.LogWarning("Cloud returned no profile for {DstId}", local.DstId);
            return null;
        }

        var data = BuildFromCloud(srcId, summary);

        try { await _cache.SetAsync(srcId, data); }
        catch (Exception ex) { _logger.LogWarning(ex, "Cache SET failed while warming {SrcId}", srcId); }

        return data;
    }

    private static CloudProfileData BuildFromCloud(string srcId, CloudProfileSummary s) => new()
    {
        SrcId = srcId,
        DstId = s.DstId,
        Firstname = s.GivenName,
        Lastname = s.Surname,
        Email = s.Email,
        PhoneNumber = s.PhoneNumber,
        MembershipLevel = s.LoyaltyLevel == LoyaltyLevel.None ? null : s.LoyaltyLevel.ToString(),
        MembershipId = s.LoyaltyMemberId,
        MembershipExpiredAt = s.LoyaltyExpiry?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ?? default,
    };

    private static bool TryGetLoyaltyLevel(CloudProfileData data, out LoyaltyLevel level)
    {
        if (string.IsNullOrEmpty(data.MembershipLevel))
        {
            level = LoyaltyLevel.None;
            return false;
        }
        return Enum.TryParse(data.MembershipLevel, ignoreCase: true, out level);
    }

    private static string MapStatus(string src) => src.Trim().ToUpperInvariant() switch
    {
        "CONFIRMED" => "CONFIRMED",
        "CHECKEDIN" => "CHECKED_IN",
        "CHECKEDOUT" => "CHECKED_OUT",
        "CANCELLED" or "CANCELED" => "CANCELLED",
        "NOSHOW" or "NO_SHOW" => "NO_SHOW",
        "PENDING" => "PENDING",
        _ => "CONFIRMED",
    };

    private static string PackageCodeFor(string serviceType)
    {
        // Простой мэппинг названия услуги в короткий код пакета.
        var normalized = serviceType.Trim().ToUpperInvariant().Replace(' ', '-');
        return normalized.Length > 20 ? normalized[..20] : normalized;
    }
}
