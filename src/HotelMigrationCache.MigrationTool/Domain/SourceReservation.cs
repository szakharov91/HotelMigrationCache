namespace HotelMigrationCache.MigrationTool.Domain;

public sealed record SourceReservation(
    string SourceFileName,
    string RawXmlContent,
    string BookingId,
    string MainProfileId,
    DateOnly CheckIn,
    DateOnly CheckOut,
    SourceRoomDetails Room,
    SourceGuestComposition Guests,
    string Status,
    SourceMoney TotalPrice,
    SourceMoney? DepositPaid,
    IReadOnlyList<SourceReservationService> Services,
    string? Remarks,
    IReadOnlyList<string> AccompanyingProfileIds,
    IReadOnlyList<string> AttachedCompanyProfileIds,
    string? BillingCompanyProfileId);

public sealed record SourceRoomDetails(
    string RoomType,
    string? RoomNumber,
    string? BedConfiguration);

public sealed record SourceGuestComposition(
    int AdultCount,
    int ChildCount,
    IReadOnlyList<int> ChildrenAges);

public sealed record SourceMoney(decimal Value, string Currency);

public sealed record SourceReservationService(
    string ServiceType,
    DateOnly? ServiceDate,
    bool? IsDaily,
    SourceMoney? Price,
    SourceMoney? DailyRate);
