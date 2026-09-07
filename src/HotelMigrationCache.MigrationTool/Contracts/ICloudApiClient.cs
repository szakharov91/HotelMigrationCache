using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Contracts;

/// <summary>Абстракция «облачного» REST API. Демо-реализация симулирует сетевые задержки
/// и хранит состояние в отдельной SQLite базе, чтобы кэш реально имел смысл.</summary>
public interface ICloudApiClient
{
    Task InitializeAsync(CancellationToken ct);

    // Профайл-Guest — 4 подшага (создание + контакты + реквизиты + атрибуты).
    Task<string> CreateProfileAsync(CloudCreateProfileRequest request, CancellationToken ct);
    Task AddProfileContactsAsync(string dstId, CloudProfileContacts contacts, CancellationToken ct);
    Task AddProfileRequisitesAsync(string dstId, CloudProfileRequisites requisites, CancellationToken ct);
    Task AddProfileAttributesAsync(string dstId, CloudProfileAttributes attributes, CancellationToken ct);

    // Профайл-Company/Agent — CreateCompany + те же общие подшаги + AR + NegotiatedRates.
    Task<string> CreateCompanyProfileAsync(CloudCreateCompanyProfileRequest request, CancellationToken ct);
    Task UpdateProfileWithArAddressAsync(string dstId, CloudArAddressUpdateRequest request, CancellationToken ct);
    Task CreateArAccountAsync(CloudArAccountRequest request, CancellationToken ct);
    Task AddNegotiatedRatesAsync(string dstId, IReadOnlyList<CloudNegotiatedRate> rates, CancellationToken ct);

    // Relationships между профайлами — batch-этап после Migrate Profiles.
    Task AddProfileRelationshipAsync(CloudProfileRelationshipRequest request, CancellationToken ct);

    // Fallback: клиент может спросить облако при промахе кэша.
    Task<CloudProfileSummary?> GetProfileByDstIdAsync(string dstId, CancellationToken ct);

    // Бронирование — расширенный поток: Create → Confirmation → AttachStayProfiles → Update → Cancel.
    Task<string> CreateReservationAsync(CloudCreateReservationRequest request, CancellationToken ct);
    Task<CloudReservationConfirmation> GetReservationConfirmationAsync(string reservationDstId, CancellationToken ct);
    Task AttachStayProfileAsync(CloudStayProfileAttachment attachment, CancellationToken ct);
    Task UpdateReservationAsync(CloudReservationUpdateRequest request, CancellationToken ct);
    Task AddAccompanyingGuestAsync(string reservationDstId, string guestDstId, CancellationToken ct);
    Task UpdateRatesForLoyaltyAsync(string reservationDstId, LoyaltyLevel level, CancellationToken ct);
    Task CancelReservationAsync(string reservationDstId, string reason, CancellationToken ct);

    // Справочные данные отеля (read-only). Отличный кандидат на reference-cache:
    // уникальных значений мало, а lookup вызывается на каждую бронь / профайл.
    Task<CloudRoomTypeInfo> GetRoomTypeInfoAsync(string code, CancellationToken ct);
    Task<CloudRateCodeInfo> GetRateCodeInfoAsync(string code, CancellationToken ct);
    Task<CloudLoyaltyRateRule> GetLoyaltyRateRuleAsync(LoyaltyLevel level, CancellationToken ct);
    Task<CloudPaymentTypeInfo> GetPaymentTypeInfoAsync(string currency, CancellationToken ct);
    Task<CloudPreferenceMapping> GetPreferenceMappingAsync(string category, string sourceCode, CancellationToken ct);
}

// Ответ облака при чтении профайла — то, что понадобится, чтобы прогреть кэш.
public sealed record CloudProfileSummary(
    string DstId,
    string GivenName,
    string Surname,
    DateOnly DateOfBirth,
    string? Email,
    string? PhoneNumber,
    LoyaltyLevel LoyaltyLevel,
    string? LoyaltyMemberId,
    DateOnly? LoyaltyExpiry);
