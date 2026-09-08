using System.Globalization;
using System.Text.Json;
using Dapper;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using HotelMigrationCache.MigrationTool.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.CloudApi;

/// <summary>
/// «Облако» в процессе с полной эмуляцией: собственная SQLite, задержка на каждый вызов,
/// dstId-ы генерирует сама облачная сторона. Таблицы охватывают все шаги реального сценария:
/// профайлы (гости + компании), AR-аккаунты, negotiated rates, связи, брони с confirmation,
/// stay-профайлы, сопровождающие гости.
/// </summary>
public sealed class SimulatedCloudApiClient : ICloudApiClient
{
    private readonly CloudApiOptions _options;
    private readonly IMigrationStatistics _stats;
    private readonly ILogger<SimulatedCloudApiClient> _logger;
    private readonly string _connectionString;

    public SimulatedCloudApiClient(CloudApiOptions options, IMigrationStatistics stats, ILogger<SimulatedCloudApiClient> logger)
    {
        _options = options;
        _stats = stats;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.CloudDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_options.CloudDbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");

        // Гости и компании в одной таблице (Kind = Guest/Company/Agent), чтобы упростить lookup by DstId.
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudProfiles (
                DstId            TEXT PRIMARY KEY,
                Kind             TEXT NOT NULL,
                SrcGuestId       TEXT NOT NULL,
                GivenName        TEXT NOT NULL,
                Surname          TEXT NOT NULL,
                Gender           TEXT NULL,
                DateOfBirth      TEXT NULL,
                Citizenship      TEXT NULL,
                CorporateId      TEXT NULL,
                PhoneNumber      TEXT NULL,
                EmailAddress     TEXT NULL,
                Street           TEXT NULL,
                Locality         TEXT NULL,
                ZipCode          TEXT NULL,
                CountryCode      TEXT NULL,
                DocumentType     TEXT NULL,
                DocumentNumber   TEXT NULL,
                IssuedOn         TEXT NULL,
                ExpiresOn        TEXT NULL,
                IssuingAuthority TEXT NULL,
                PrefLanguage     TEXT NULL,
                PrefSmoking      TEXT NULL,
                PrefBed          TEXT NULL,
                LoyaltyLevel     INTEGER NULL,
                LoyaltyMemberId  TEXT NULL,
                LoyaltyExpiry    TEXT NULL,
                ArAddressLine    TEXT NULL,
                CreatedAtUtc     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CloudProfiles_SrcGuestId ON CloudProfiles(SrcGuestId);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudArAccounts (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                CompanyDstId  TEXT NOT NULL,
                ArNumber      TEXT NOT NULL,
                ArAddressLine TEXT NOT NULL,
                CreatedAtUtc  TEXT NOT NULL,
                FOREIGN KEY(CompanyDstId) REFERENCES CloudProfiles(DstId)
            );
            CREATE INDEX IF NOT EXISTS IX_CloudArAccounts_CompanyDstId ON CloudArAccounts(CompanyDstId);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudNegotiatedRates (
                Id              INTEGER PRIMARY KEY AUTOINCREMENT,
                CompanyDstId    TEXT NOT NULL,
                RateCode        TEXT NOT NULL,
                DiscountPercent INTEGER NOT NULL,
                CreatedAtUtc    TEXT NOT NULL,
                FOREIGN KEY(CompanyDstId) REFERENCES CloudProfiles(DstId)
            );
            CREATE INDEX IF NOT EXISTS IX_CloudNegotiatedRates_CompanyDstId ON CloudNegotiatedRates(CompanyDstId);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudProfileRelationships (
                Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceProfileDstId   TEXT NOT NULL,
                TargetProfileDstId   TEXT NOT NULL,
                RelationType         TEXT NOT NULL,
                CreatedAtUtc         TEXT NOT NULL,
                UNIQUE(SourceProfileDstId, TargetProfileDstId, RelationType)
            );
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudReservations (
                DstId                  TEXT PRIMARY KEY,
                SrcReservationId       TEXT NOT NULL,
                MainGuestDstId         TEXT NOT NULL,
                ConfirmationNumber     TEXT NULL,
                ArrivalDate            TEXT NOT NULL,
                DepartureDate          TEXT NOT NULL,
                RoomCategory           TEXT NOT NULL,
                UnitNumber             TEXT NULL,
                BedSetup               TEXT NULL,
                Adults                 INTEGER NOT NULL,
                Children               INTEGER NOT NULL,
                ChildAgesJson          TEXT NOT NULL,
                ReservationStatus      TEXT NOT NULL,
                TotalValue             TEXT NOT NULL,
                TotalCurrency          TEXT NOT NULL,
                PrepaidValue           TEXT NULL,
                PrepaidCurrency        TEXT NULL,
                Remarks                TEXT NULL,
                RateLoyaltyLevel       INTEGER NULL,
                BillingCompanyDstId    TEXT NULL,
                CancellationReason     TEXT NULL,
                CreatedAtUtc           TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CloudReservations_SrcReservationId ON CloudReservations(SrcReservationId);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudReservationGuests (
                ReservationDstId TEXT NOT NULL,
                GuestDstId       TEXT NOT NULL,
                PRIMARY KEY(ReservationDstId, GuestDstId),
                FOREIGN KEY(ReservationDstId) REFERENCES CloudReservations(DstId)
            );
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudReservationStayProfiles (
                ReservationDstId TEXT NOT NULL,
                ProfileDstId     TEXT NOT NULL,
                Role             TEXT NOT NULL,
                PRIMARY KEY(ReservationDstId, ProfileDstId),
                FOREIGN KEY(ReservationDstId) REFERENCES CloudReservations(DstId)
            );
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS CloudReservationPackages (
                Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                ReservationDstId TEXT NOT NULL,
                PackageCode      TEXT NOT NULL,
                Amount           TEXT NOT NULL,
                FOREIGN KEY(ReservationDstId) REFERENCES CloudReservations(DstId)
            );
        ");

        _logger.LogInformation("Cloud DB ready at {Path}", _options.CloudDbPath);
    }

    // ---------- Profile: Guest ----------

    public async Task<string> CreateProfileAsync(CloudCreateProfileRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        var dstId = NewProfileDstId();

        const string sql = @"
            INSERT INTO CloudProfiles
                (DstId, Kind, SrcGuestId, GivenName, Surname, Gender, DateOfBirth, Citizenship, CreatedAtUtc)
            VALUES
                (@DstId, 'Guest', @SrcGuestId, @GivenName, @Surname, @Gender, @DateOfBirth, @Citizenship, @CreatedAtUtc);";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            DstId = dstId,
            request.SrcGuestId,
            request.GivenName,
            request.Surname,
            request.Gender,
            DateOfBirth = FormatDate(request.DateOfBirth),
            request.Citizenship,
            CreatedAtUtc = NowUtc(),
        }, cancellationToken: ct));

        return dstId;
    }

    // ---------- Profile: Company / Agent ----------

    public async Task<string> CreateCompanyProfileAsync(CloudCreateCompanyProfileRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        var dstId = NewProfileDstId();

        const string sql = @"
            INSERT INTO CloudProfiles
                (DstId, Kind, SrcGuestId, GivenName, Surname, CorporateId, CreatedAtUtc)
            VALUES
                (@DstId, @Kind, @SrcCompanyId, '', @CompanyName, @CorporateId, @CreatedAtUtc);";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            DstId = dstId,
            Kind = request.CompanyKind,
            request.SrcCompanyId,
            request.CompanyName,
            request.CorporateId,
            CreatedAtUtc = NowUtc(),
        }, cancellationToken: ct));

        return dstId;
    }

    // ---------- Общие подшаги профайла ----------

    public async Task AddProfileContactsAsync(string dstId, CloudProfileContacts contacts, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            UPDATE CloudProfiles
            SET PhoneNumber  = @PhoneNumber,
                EmailAddress = @EmailAddress,
                Street       = @Street,
                Locality     = @Locality,
                ZipCode      = @ZipCode,
                CountryCode  = @CountryCode
            WHERE DstId = @DstId;";

        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            DstId = dstId,
            contacts.PhoneNumber,
            contacts.EmailAddress,
            Street = contacts.PostalAddress.StreetLine,
            contacts.PostalAddress.Locality,
            contacts.PostalAddress.ZipCode,
            contacts.PostalAddress.CountryCode,
        }, cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"AddContacts: profile {dstId} not found");
    }

    public async Task AddProfileRequisitesAsync(string dstId, CloudProfileRequisites requisites, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            UPDATE CloudProfiles
            SET DocumentType     = @DocumentType,
                DocumentNumber   = @DocumentNumber,
                IssuedOn         = @IssuedOn,
                ExpiresOn        = @ExpiresOn,
                IssuingAuthority = @IssuingAuthority
            WHERE DstId = @DstId;";

        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            DstId = dstId,
            requisites.DocumentType,
            requisites.DocumentNumber,
            IssuedOn = FormatDate(requisites.IssuedOn),
            ExpiresOn = FormatDate(requisites.ExpiresOn),
            requisites.IssuingAuthority,
        }, cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"AddRequisites: profile {dstId} not found");
    }

    public async Task AddProfileAttributesAsync(string dstId, CloudProfileAttributes attributes, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            UPDATE CloudProfiles
            SET PrefLanguage    = @PrefLanguage,
                PrefSmoking     = @PrefSmoking,
                PrefBed         = @PrefBed,
                LoyaltyLevel    = @LoyaltyLevel,
                LoyaltyMemberId = @LoyaltyMemberId,
                LoyaltyExpiry   = @LoyaltyExpiry
            WHERE DstId = @DstId;";

        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            DstId = dstId,
            PrefLanguage = attributes.Preferences?.Language,
            PrefSmoking = attributes.Preferences?.Smoking,
            PrefBed = attributes.Preferences?.PreferredBed,
            LoyaltyLevel = attributes.Loyalty is null ? (int?)null : (int)attributes.Loyalty.Level,
            LoyaltyMemberId = attributes.Loyalty?.MemberId,
            LoyaltyExpiry = attributes.Loyalty is null ? null : FormatDate(attributes.Loyalty.ExpiryDate),
        }, cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"AddAttributes: profile {dstId} not found");
    }

    // ---------- AR-аккаунт (двухшаговый: Update address + Create AR) ----------

    public async Task UpdateProfileWithArAddressAsync(string dstId, CloudArAddressUpdateRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"UPDATE CloudProfiles SET ArAddressLine = @ArAddressLine WHERE DstId = @DstId;";
        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { DstId = dstId, request.ArAddressLine },
            cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"UpdateProfileWithArAddress: profile {dstId} not found");
    }

    public async Task CreateArAccountAsync(CloudArAccountRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            INSERT INTO CloudArAccounts (CompanyDstId, ArNumber, ArAddressLine, CreatedAtUtc)
            VALUES (@CompanyDstId, @ArNumber, @ArAddressLine, @CreatedAtUtc);";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            request.CompanyDstId,
            request.ArNumber,
            request.ArAddressLine,
            CreatedAtUtc = NowUtc(),
        }, cancellationToken: ct));
    }

    // ---------- Negotiated rates ----------

    public async Task AddNegotiatedRatesAsync(string dstId, IReadOnlyList<CloudNegotiatedRate> rates, CancellationToken ct)
    {
        if (rates.Count == 0) return;
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            INSERT INTO CloudNegotiatedRates (CompanyDstId, RateCode, DiscountPercent, CreatedAtUtc)
            VALUES (@CompanyDstId, @RateCode, @DiscountPercent, @CreatedAtUtc);";

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var r in rates)
        {
            await conn.ExecuteAsync(new CommandDefinition(sql, new
            {
                CompanyDstId = dstId,
                r.RateCode,
                r.DiscountPercent,
                CreatedAtUtc = NowUtc(),
            }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct);
    }

    // ---------- Relationships ----------

    public async Task AddProfileRelationshipAsync(CloudProfileRelationshipRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            INSERT INTO CloudProfileRelationships (SourceProfileDstId, TargetProfileDstId, RelationType, CreatedAtUtc)
            VALUES (@SourceProfileDstId, @TargetProfileDstId, @RelationType, @CreatedAtUtc)
            ON CONFLICT DO NOTHING;";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            request.SourceProfileDstId,
            request.TargetProfileDstId,
            request.RelationType,
            CreatedAtUtc = NowUtc(),
        }, cancellationToken: ct));
    }

    // ---------- Fallback чтения профайла ----------

    public async Task<CloudProfileSummary?> GetProfileByDstIdAsync(string dstId, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            SELECT DstId, GivenName, Surname, DateOfBirth, EmailAddress, PhoneNumber,
                   LoyaltyLevel, LoyaltyMemberId, LoyaltyExpiry
            FROM CloudProfiles
            WHERE DstId = @DstId
            LIMIT 1;";

        await using var conn = await OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<CloudProfileRow>(
            new CommandDefinition(sql, new { DstId = dstId }, cancellationToken: ct));

        if (row is null)
            return null;

        return new CloudProfileSummary(
            DstId: row.DstId,
            GivenName: row.GivenName,
            Surname: row.Surname,
            DateOfBirth: string.IsNullOrEmpty(row.DateOfBirth) ? default : ParseDate(row.DateOfBirth),
            Email: row.EmailAddress,
            PhoneNumber: row.PhoneNumber,
            LoyaltyLevel: row.LoyaltyLevel is null ? LoyaltyLevel.None : (LoyaltyLevel)row.LoyaltyLevel.Value,
            LoyaltyMemberId: row.LoyaltyMemberId,
            LoyaltyExpiry: row.LoyaltyExpiry is null ? null : ParseDate(row.LoyaltyExpiry));
    }

    // ---------- Reservation ----------

    public async Task<string> CreateReservationAsync(CloudCreateReservationRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        var dstId = NewReservationDstId();

        const string sql = @"
            INSERT INTO CloudReservations
                (DstId, SrcReservationId, MainGuestDstId, ArrivalDate, DepartureDate,
                 RoomCategory, UnitNumber, BedSetup,
                 Adults, Children, ChildAgesJson,
                 ReservationStatus, TotalValue, TotalCurrency,
                 PrepaidValue, PrepaidCurrency, Remarks, CreatedAtUtc)
            VALUES
                (@DstId, @SrcReservationId, @MainGuestDstId, @ArrivalDate, @DepartureDate,
                 @RoomCategory, @UnitNumber, @BedSetup,
                 @Adults, @Children, @ChildAgesJson,
                 @ReservationStatus, @TotalValue, @TotalCurrency,
                 @PrepaidValue, @PrepaidCurrency, @Remarks, @CreatedAtUtc);";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            DstId = dstId,
            request.SrcReservationId,
            request.MainGuestDstId,
            ArrivalDate = FormatDate(request.ArrivalDate),
            DepartureDate = FormatDate(request.DepartureDate),
            request.RoomCategory,
            request.UnitNumber,
            request.BedSetup,
            request.Adults,
            request.Children,
            ChildAgesJson = JsonSerializer.Serialize(request.ChildAges),
            request.ReservationStatus,
            TotalValue = request.TotalValue.ToString(CultureInfo.InvariantCulture),
            request.TotalCurrency,
            PrepaidValue = request.PrepaidValue?.ToString(CultureInfo.InvariantCulture),
            request.PrepaidCurrency,
            request.Remarks,
            CreatedAtUtc = NowUtc(),
        }, cancellationToken: ct));

        return dstId;
    }

    public async Task<CloudReservationConfirmation> GetReservationConfirmationAsync(string reservationDstId, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        // «Облако» присваивает confirmation number после создания.
        var confirmation = $"CFN-{Guid.NewGuid():N}"[..12];

        const string sql = @"UPDATE CloudReservations SET ConfirmationNumber = @Cnf WHERE DstId = @DstId;";
        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { DstId = reservationDstId, Cnf = confirmation },
            cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"GetReservationConfirmation: reservation {reservationDstId} not found");

        return new CloudReservationConfirmation(reservationDstId, confirmation);
    }

    public async Task AttachStayProfileAsync(CloudStayProfileAttachment attachment, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            INSERT INTO CloudReservationStayProfiles (ReservationDstId, ProfileDstId, Role)
            VALUES (@ReservationDstId, @ProfileDstId, @Role)
            ON CONFLICT DO NOTHING;";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new
        {
            attachment.ReservationDstId,
            attachment.ProfileDstId,
            Role = attachment.Role.ToString(),
        }, cancellationToken: ct));
    }

    public async Task UpdateReservationAsync(CloudReservationUpdateRequest request, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        if (request.BillingCompanyDstId is not null)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"UPDATE CloudReservations SET BillingCompanyDstId = @Billing WHERE DstId = @DstId;",
                new { DstId = request.ReservationDstId, Billing = request.BillingCompanyDstId },
                tx, cancellationToken: ct));
        }

        foreach (var pkg in request.Packages)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                @"INSERT INTO CloudReservationPackages (ReservationDstId, PackageCode, Amount) VALUES (@R, @C, @A);",
                new { R = request.ReservationDstId, C = pkg.PackageCode, A = pkg.Amount.ToString(CultureInfo.InvariantCulture) },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task AddAccompanyingGuestAsync(string reservationDstId, string guestDstId, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            INSERT INTO CloudReservationGuests (ReservationDstId, GuestDstId)
            VALUES (@ReservationDstId, @GuestDstId)
            ON CONFLICT DO NOTHING;";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { ReservationDstId = reservationDstId, GuestDstId = guestDstId },
            cancellationToken: ct));
    }

    public async Task UpdateRatesForLoyaltyAsync(string reservationDstId, LoyaltyLevel level, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"UPDATE CloudReservations SET RateLoyaltyLevel = @Level WHERE DstId = @DstId;";
        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { DstId = reservationDstId, Level = (int)level },
            cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"UpdateRates: reservation {reservationDstId} not found");
    }

    public async Task CancelReservationAsync(string reservationDstId, string reason, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);

        const string sql = @"
            UPDATE CloudReservations
            SET ReservationStatus = 'CANCELLED', CancellationReason = @Reason
            WHERE DstId = @DstId;";
        await using var conn = await OpenAsync(ct);
        var rows = await conn.ExecuteAsync(new CommandDefinition(sql,
            new { DstId = reservationDstId, Reason = reason },
            cancellationToken: ct));

        if (rows == 0)
            throw CloudException($"Cancel: reservation {reservationDstId} not found");
    }

    // ---------- Справочные данные (in-memory, детерминированные) ----------

    public async Task<CloudRoomTypeInfo> GetRoomTypeInfoAsync(string code, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);
        var category = code.Contains("Suite", StringComparison.OrdinalIgnoreCase) ? "Suite" : "Standard";
        return new CloudRoomTypeInfo { Code = code, Category = category, AllowsExtraBed = category == "Standard" };
    }

    public async Task<CloudRateCodeInfo> GetRateCodeInfoAsync(string code, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);
        return new CloudRateCodeInfo { Code = code, Description = $"Corporate rate {code}" };
    }

    public async Task<CloudLoyaltyRateRule> GetLoyaltyRateRuleAsync(LoyaltyLevel level, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);
        var discount = level switch
        {
            LoyaltyLevel.Diamond => 25,
            LoyaltyLevel.Platinum => 20,
            LoyaltyLevel.Gold => 15,
            LoyaltyLevel.Silver => 10,
            LoyaltyLevel.Basic => 5,
            _ => 0,
        };
        return new CloudLoyaltyRateRule { Level = level, DiscountPercent = discount };
    }

    public async Task<CloudPaymentTypeInfo> GetPaymentTypeInfoAsync(string currency, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);
        return new CloudPaymentTypeInfo { Currency = currency, AcceptorNetwork = "Visa/MC" };
    }

    public async Task<CloudPreferenceMapping> GetPreferenceMappingAsync(string category, string sourceCode, CancellationToken ct)
    {
        using var _cloudScope = _stats.TrackCloudOperation();
        await SimulateLatencyAsync(ct);
        // Возвращаем канонический код (upper-case), как если бы это был лукап в конфигурации отеля.
        return new CloudPreferenceMapping { Category = category, SourceCode = sourceCode, CanonicalCode = sourceCode.ToUpperInvariant() };
    }

    // ---------- helpers ----------

    private async Task SimulateLatencyAsync(CancellationToken ct)
    {
        var min = Math.Max(0, _options.MinLatencyMs);
        var max = Math.Max(min + 1, _options.MaxLatencyMs);
        var delayMs = Random.Shared.Next(min, max);
        if (delayMs > 0)
            await Task.Delay(delayMs, ct);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static string NewProfileDstId() => $"CLD-P-{Guid.NewGuid():N}"[..14];
    private static string NewReservationDstId() => $"CLD-R-{Guid.NewGuid():N}"[..14];
    private static string NowUtc() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static InvalidOperationException CloudException(string msg) => new($"[CloudApi] {msg}");

    // Positional record — Dapper связывается через конструктор, никаких unread-сеттеров.
    private sealed record CloudProfileRow(
        string DstId,
        string GivenName,
        string Surname,
        string? DateOfBirth,
        string? EmailAddress,
        string? PhoneNumber,
        long? LoyaltyLevel,
        string? LoyaltyMemberId,
        string? LoyaltyExpiry);
}
