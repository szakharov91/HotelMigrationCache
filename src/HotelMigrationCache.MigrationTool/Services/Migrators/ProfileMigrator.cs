using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using HotelMigrationCache.MigrationTool.Services.Loader;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Migrators;

// Ветвится по ProfileType.
//   Guest       : Create → Contacts → Requisites → Attributes (с loyalty)
//   Company/Agent: CreateCompany → Contacts → Requisites → Attributes (без loyalty)
//                  → (если AR: UpdateWithArAddress + CreateArAccount)
//                  → (если negotiated rates: AddRates)
// В конце: прогрев кэша + очередь Relationships (batch-этап обработает позже).
public sealed class ProfileMigrator : IProfileMigrator
{
    private readonly ICloudApiClient _cloud;
    private readonly ICacheService _cache;
    private readonly IReferenceCache _refCache;
    private readonly IRelationshipQueue _relationships;
    private readonly ILogger<ProfileMigrator> _logger;

    public ProfileMigrator(
        ICloudApiClient cloud,
        ICacheService cache,
        IReferenceCache refCache,
        IRelationshipQueue relationships,
        ILogger<ProfileMigrator> logger)
    {
        _cloud = cloud;
        _cache = cache;
        _refCache = refCache;
        _relationships = relationships;
        _logger = logger;
    }

    public async Task<string?> MigrateAsync(ProfileRecord record, CancellationToken ct)
    {
        SourceProfile source;
        try
        {
            source = SourceXmlParser.ParseProfile(record.FileRawContent, record.SourceFilename);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse failed for profile {SrcId}", record.SrcId);
            return null;
        }

        string dstId;
        try
        {
            dstId = source.Type == ProfileType.Guest
                ? await MigrateGuestAsync(source, ct)
                : await MigrateCompanyAsync(source, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cloud call failed for profile {SrcId}", record.SrcId);
            return null;
        }

        // Прогреваем кэш. Ошибка кэша не отменяет успех миграции.
        try
        {
            var data = CloudProfileDataBuilder.Build(source.ProfileId, dstId, source);
            await _cache.SetAsync(source.ProfileId, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache warm failed for profile {SrcId}", record.SrcId);
        }

        // Складываем связи в очередь для batch-этапа. Ошибки очереди не фатальны.
        if (source.Relationships.Count > 0)
        {
            try
            {
                var items = source.Relationships
                    .Select(r => new PendingRelationship(0, source.ProfileId, r.RelatedProfileId, r.RelationType))
                    .ToList();
                await _relationships.EnqueueBatchAsync(items, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue relationships for {SrcId}", record.SrcId);
            }
        }

        return dstId;
    }

    private async Task<string> MigrateGuestAsync(SourceProfile source, CancellationToken ct)
    {
        // 1/4 — Create
        var dstId = await _cloud.CreateProfileAsync(new CloudCreateProfileRequest(
            SrcGuestId: source.ProfileId,
            GivenName: source.Personal.FirstName,
            Surname: source.Personal.LastName,
            Gender: source.Personal.Gender,
            DateOfBirth: source.Personal.BirthDate,
            Citizenship: source.Personal.Nationality),
            ct);

        // 2/4 — Contacts
        await _cloud.AddProfileContactsAsync(dstId, BuildContacts(source), ct);

        // 3/4 — Requisites
        await _cloud.AddProfileRequisitesAsync(dstId, BuildRequisites(source), ct);

        // Валидация preference-кодов через reference cache (первый уникальный код → облако, дальше hit).
        await ValidatePreferencesAsync(source.Preferences, ct);

        // 4/4 — Attributes (preferences + loyalty)
        var attrs = new CloudProfileAttributes(
            Preferences: BuildPreferences(source),
            Loyalty: source.Loyalty is null ? null : new CloudLoyaltyMembership(
                source.Loyalty.Level, source.Loyalty.MemberId, source.Loyalty.ExpiryDate));
        await _cloud.AddProfileAttributesAsync(dstId, attrs, ct);

        return dstId;
    }

    // Sequential await по каждому rate-коду — reference cache и так дедуплицирует.
#pragma warning disable S3267 // «Use LINQ»: LINQ+await неудобно, оставляем цикл.
    private async Task ValidateRateCodesAsync(IReadOnlyList<SourceNegotiatedRate> rates, CancellationToken ct)
    {
        foreach (var r in rates)
            await _refCache.GetOrLoadAsync($"rate:{r.RateCode}",
                c => _cloud.GetRateCodeInfoAsync(r.RateCode, c), ct);
    }
#pragma warning restore S3267

    // Каждый preference-код валидируется в облаке — но уникальных значений мало
    // (Language: ~1, Smoking: 2, BedType: 3-4). Reference cache даёт ~100% hit rate после первых профайлов.
    private async Task ValidatePreferencesAsync(SourcePreferences? prefs, CancellationToken ct)
    {
        if (prefs is null) return;
        if (!string.IsNullOrEmpty(prefs.Language))
            await _refCache.GetOrLoadAsync($"pref:lang:{prefs.Language}",
                c => _cloud.GetPreferenceMappingAsync("lang", prefs.Language, c), ct);
        if (!string.IsNullOrEmpty(prefs.Smoking))
            await _refCache.GetOrLoadAsync($"pref:smoke:{prefs.Smoking}",
                c => _cloud.GetPreferenceMappingAsync("smoke", prefs.Smoking, c), ct);
        if (!string.IsNullOrEmpty(prefs.BedType))
            await _refCache.GetOrLoadAsync($"pref:bed:{prefs.BedType}",
                c => _cloud.GetPreferenceMappingAsync("bed", prefs.BedType, c), ct);
    }

    private async Task<string> MigrateCompanyAsync(SourceProfile source, CancellationToken ct)
    {
        // 1/N — CreateCompany
        var kind = source.Type == ProfileType.Agent ? "Agent" : "Company";
        var companyName = $"{source.Personal.FirstName} {source.Personal.LastName}".Trim();
        var dstId = await _cloud.CreateCompanyProfileAsync(new CloudCreateCompanyProfileRequest(
            SrcCompanyId: source.ProfileId,
            CompanyName: companyName,
            CorporateId: source.CorporateId ?? "N/A",
            CompanyKind: kind),
            ct);

        // 2/N — Contacts
        await _cloud.AddProfileContactsAsync(dstId, BuildContacts(source), ct);

        // 3/N — Requisites
        await _cloud.AddProfileRequisitesAsync(dstId, BuildRequisites(source), ct);

        // 4/N — Attributes (preferences only; loyalty у компаний не бывает)
        var attrs = new CloudProfileAttributes(BuildPreferences(source), Loyalty: null);
        await _cloud.AddProfileAttributesAsync(dstId, attrs, ct);

        // 5-6/N — AR account: сначала обновляем профайл с AR ADDRESS, потом создаём AR.
        if (!string.IsNullOrEmpty(source.ArNumber))
        {
            var arAddressLine = $"{source.Contact.Address.Street}, {source.Contact.Address.City}";
            await _cloud.UpdateProfileWithArAddressAsync(dstId, new CloudArAddressUpdateRequest(arAddressLine), ct);
            await _cloud.CreateArAccountAsync(new CloudArAccountRequest(dstId, source.ArNumber, arAddressLine), ct);
        }

        // 7/N — Negotiated rates. Валидация RateCode через reference cache перед отправкой.
        if (source.NegotiatedRates.Count > 0)
        {
            await ValidateRateCodesAsync(source.NegotiatedRates, ct);

            var rates = source.NegotiatedRates
                .Select(r => new CloudNegotiatedRate(r.RateCode, r.DiscountPercent))
                .ToList();
            await _cloud.AddNegotiatedRatesAsync(dstId, rates, ct);
        }

        return dstId;
    }

    private static CloudProfileContacts BuildContacts(SourceProfile s) => new(
        PhoneNumber: s.Contact.Phone,
        EmailAddress: s.Contact.Email,
        PostalAddress: new CloudPostalAddress(
            StreetLine: s.Contact.Address.Street,
            Locality: s.Contact.Address.City,
            ZipCode: s.Contact.Address.PostalCode,
            CountryCode: s.Contact.Address.Country));

    private static CloudProfileRequisites BuildRequisites(SourceProfile s) => new(
        DocumentType: s.IdentityDocument.DocType,
        DocumentNumber: s.IdentityDocument.DocNumber,
        IssuedOn: s.IdentityDocument.IssueDate,
        ExpiresOn: s.IdentityDocument.ExpiryDate,
        IssuingAuthority: s.IdentityDocument.IssuedBy);

    private static CloudGuestPreferences? BuildPreferences(SourceProfile s) =>
        s.Preferences is null ? null : new CloudGuestPreferences(
            Language: s.Preferences.Language,
            Smoking: s.Preferences.Smoking,
            PreferredBed: s.Preferences.BedType);
}
