using System.Runtime.CompilerServices;
using Dapper;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using HotelMigrationCache.MigrationTool.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Storage;

public sealed class SqliteMigrationRepository : IMigrationRepository
{
    private readonly MigrationOptions _options;
    private readonly IMigrationStatistics _stats;
    private readonly ILogger<SqliteMigrationRepository> _logger;
    private readonly string _connectionString;

    public SqliteMigrationRepository(MigrationOptions options, IMigrationStatistics stats, ILogger<SqliteMigrationRepository> logger)
    {
        _options = options;
        _stats = stats;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _options.MigrationDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeSchemaAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_options.MigrationDbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var conn = await OpenAsync(ct);

        // WAL — параллельные чтения при пишущем; фиксированный foreign_keys — на всякий случай.
        await conn.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Profiles (
                Id             INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceFilename TEXT    NOT NULL,
                FileRawContent TEXT    NOT NULL,
                SrcId          TEXT    NOT NULL UNIQUE,
                DstId          TEXT    NULL,
                State          INTEGER NOT NULL,
                Error          TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Profiles_State ON Profiles(State);
        ");

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Reservations (
                Id               INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceFilename   TEXT    NOT NULL,
                FileRawContent   TEXT    NOT NULL,
                SrcId            TEXT    NOT NULL UNIQUE,
                DstId            TEXT    NULL,
                State            INTEGER NOT NULL,
                Error            TEXT    NULL,
                MainProfileSrcId TEXT    NOT NULL,
                MainProfileDstId TEXT    NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Reservations_State ON Reservations(State);
            CREATE INDEX IF NOT EXISTS IX_Reservations_MainProfileSrcId ON Reservations(MainProfileSrcId);
        ");

        _logger.LogInformation("Migration DB ready at {Path}", _options.MigrationDbPath);
    }

    public async Task InsertProfilesIfMissingAsync(IReadOnlyCollection<ProfileRecord> records, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        if (records.Count == 0) return;

        const string sql = @"
            INSERT INTO Profiles (SourceFilename, FileRawContent, SrcId, DstId, State, Error)
            VALUES (@SourceFilename, @FileRawContent, @SrcId, @DstId, @State, @Error)
            ON CONFLICT(SrcId) DO NOTHING;";

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, records, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task InsertReservationsIfMissingAsync(IReadOnlyCollection<ReservationRecord> records, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        if (records.Count == 0) return;

        const string sql = @"
            INSERT INTO Reservations
                (SourceFilename, FileRawContent, SrcId, DstId, State, Error, MainProfileSrcId, MainProfileDstId)
            VALUES
                (@SourceFilename, @FileRawContent, @SrcId, @DstId, @State, @Error, @MainProfileSrcId, @MainProfileDstId)
            ON CONFLICT(SrcId) DO NOTHING;";

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, records, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task UpdateProfileStateAsync(string srcId, MigrationState state, string? dstId, string? error, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            UPDATE Profiles
            SET State = @State,
                DstId = COALESCE(@DstId, DstId),
                Error = @Error
            WHERE SrcId = @SrcId;";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { SrcId = srcId, State = (int)state, DstId = dstId, Error = error },
            cancellationToken: ct));
    }

    public async Task<ProfileRecord?> GetProfileBySrcIdAsync(string srcId, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            SELECT Id, SourceFilename, FileRawContent, SrcId, DstId, State, Error
            FROM Profiles
            WHERE SrcId = @SrcId
            LIMIT 1;";

        await using var conn = await OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ProfileRecord>(
            new CommandDefinition(sql, new { SrcId = srcId }, cancellationToken: ct));
    }

    public async IAsyncEnumerable<ProfileRecord> GetPendingProfilesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            SELECT Id, SourceFilename, FileRawContent, SrcId, DstId, State, Error
            FROM Profiles
            WHERE State IN (0, 1)
            ORDER BY Id;";

        await using var conn = await OpenAsync(ct);
        // Buffered=false — стримим построчно, память под 5k строк не нужна.
        var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, cancellationToken: ct));
        var parser = reader.GetRowParser<ProfileRecord>();
        while (await reader.ReadAsync(ct))
            yield return parser(reader);
    }

    public async Task<MigrationCounters> GetProfileCountersAsync(CancellationToken ct)
        => await ReadCountersAsync("Profiles", ct);

    public async Task UpdateReservationStateAsync(string srcId, MigrationState state, string? dstId, string? mainProfileDstId, string? error, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            UPDATE Reservations
            SET State = @State,
                DstId = COALESCE(@DstId, DstId),
                MainProfileDstId = COALESCE(@MainProfileDstId, MainProfileDstId),
                Error = @Error
            WHERE SrcId = @SrcId;";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { SrcId = srcId, State = (int)state, DstId = dstId, MainProfileDstId = mainProfileDstId, Error = error },
            cancellationToken: ct));
    }

    public async IAsyncEnumerable<ReservationRecord> GetPendingReservationsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            SELECT Id, SourceFilename, FileRawContent, SrcId, DstId, State, Error, MainProfileSrcId, MainProfileDstId
            FROM Reservations
            WHERE State IN (0, 1)
            ORDER BY Id;";

        await using var conn = await OpenAsync(ct);
        var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, cancellationToken: ct));
        var parser = reader.GetRowParser<ReservationRecord>();
        while (await reader.ReadAsync(ct))
            yield return parser(reader);
    }

    public async Task<MigrationCounters> GetReservationCountersAsync(CancellationToken ct)
        => await ReadCountersAsync("Reservations", ct);

    private async Task<MigrationCounters> ReadCountersAsync(string table, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        // Табличное имя — из безопасного списка (только Profiles/Reservations в этой репе), SQL-инъекции нет.
        var sql = $@"
            SELECT
                COUNT(*)                                                            AS Total,
                SUM(CASE WHEN State = {(int)MigrationState.New}        THEN 1 ELSE 0 END) AS New,
                SUM(CASE WHEN State = {(int)MigrationState.Processing} THEN 1 ELSE 0 END) AS Processing,
                SUM(CASE WHEN State = {(int)MigrationState.Success}    THEN 1 ELSE 0 END) AS Success,
                SUM(CASE WHEN State = {(int)MigrationState.Failed}     THEN 1 ELSE 0 END) AS Failed
            FROM {table};";

        await using var conn = await OpenAsync(ct);
        return await conn.QuerySingleAsync<MigrationCounters>(new CommandDefinition(sql, cancellationToken: ct));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
