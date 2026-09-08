using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.SourceGen.Attributes;

namespace HotelMigrationCache.MigrationTool.Domain;

// DTO для облачных вызовов. По одному record на явный подшаг миграции.

// --- Профайл: Guest ---
public sealed record CloudCreateProfileRequest(
    string SrcGuestId,
    string GivenName,
    string Surname,
    string Gender,
    DateOnly DateOfBirth,
    string Citizenship);

// --- Профайл: Company / Agent ---
public sealed record CloudCreateCompanyProfileRequest(
    string SrcCompanyId,
    string CompanyName,
    string CorporateId,
    string CompanyKind); // "Company" / "Agent"

// --- Общие для профайла ---
public sealed record CloudProfileContacts(
    string PhoneNumber,
    string EmailAddress,
    CloudPostalAddress PostalAddress);

public sealed record CloudPostalAddress(
    string StreetLine,
    string Locality,
    string ZipCode,
    string CountryCode);

public sealed record CloudProfileRequisites(
    string DocumentType,
    string DocumentNumber,
    DateOnly IssuedOn,
    DateOnly ExpiresOn,
    string IssuingAuthority);

public sealed record CloudProfileAttributes(
    CloudGuestPreferences? Preferences,
    CloudLoyaltyMembership? Loyalty);

public sealed record CloudGuestPreferences(
    string? Language,
    string? Smoking,
    string? PreferredBed);

public sealed record CloudLoyaltyMembership(
    LoyaltyLevel Level,
    string MemberId,
    DateOnly ExpiryDate);

// --- AR-аккаунт (только для company) ---
public sealed record CloudArAddressUpdateRequest(string ArAddressLine);

public sealed record CloudArAccountRequest(
    string CompanyDstId,
    string ArNumber,
    string ArAddressLine);

// --- Negotiated rates (только для company) ---
public sealed record CloudNegotiatedRate(string RateCode, int DiscountPercent);

// --- Relationship между двумя профайлами ---
public sealed record CloudProfileRelationshipRequest(
    string SourceProfileDstId,
    string TargetProfileDstId,
    string RelationType);

// --- Бронирование ---
public sealed record CloudCreateReservationRequest(
    string SrcReservationId,
    string MainGuestDstId,
    DateOnly ArrivalDate,
    DateOnly DepartureDate,
    string RoomCategory,
    string? UnitNumber,
    string? BedSetup,
    int Adults,
    int Children,
    IReadOnlyList<int> ChildAges,
    string ReservationStatus,
    decimal TotalValue,
    string TotalCurrency,
    decimal? PrepaidValue,
    string? PrepaidCurrency,
    string? Remarks);

public sealed record CloudReservationConfirmation(string ReservationDstId, string ConfirmationNumber);

public sealed record CloudReservationUpdateRequest(
    string ReservationDstId,
    string? BillingCompanyDstId,
    IReadOnlyList<CloudReservationPackage> Packages);

public sealed record CloudReservationPackage(string PackageCode, decimal Amount);

public enum CloudStayProfileRole { Company, Agent, Source }

public sealed record CloudStayProfileAttachment(
    string ReservationDstId,
    string ProfileDstId,
    CloudStayProfileRole Role);

// --- Справочные данные отеля: конфигурация, живёт в облаке и одинакова для всех броней ---
// Read-only по семантике; технически — mutable partial-class'ы, чтобы source-gen мог сгенерировать
// параметрический конструктор и сериализацию через public get;set; свойства.

[GenerateBinarySerializer]
public sealed partial class CloudRoomTypeInfo : IBinarySerializable<CloudRoomTypeInfo>
{
    public string Code { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool AllowsExtraBed { get; set; }
}

[GenerateBinarySerializer]
public sealed partial class CloudRateCodeInfo : IBinarySerializable<CloudRateCodeInfo>
{
    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

[GenerateBinarySerializer]
public sealed partial class CloudLoyaltyRateRule : IBinarySerializable<CloudLoyaltyRateRule>
{
    public LoyaltyLevel Level { get; set; }
    public int DiscountPercent { get; set; }
}

[GenerateBinarySerializer]
public sealed partial class CloudPaymentTypeInfo : IBinarySerializable<CloudPaymentTypeInfo>
{
    public string Currency { get; set; } = string.Empty;
    public string AcceptorNetwork { get; set; } = string.Empty;
}

[GenerateBinarySerializer]
public sealed partial class CloudPreferenceMapping : IBinarySerializable<CloudPreferenceMapping>
{
    public string Category { get; set; } = string.Empty;
    public string SourceCode { get; set; } = string.Empty;
    public string CanonicalCode { get; set; } = string.Empty;
}
