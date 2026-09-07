using System.Runtime.CompilerServices;
using Dapper;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Options;
using Microsoft.Data.Sqlite;

namespace HotelMigrationCache.MigrationTool.Services.Storage;

// Хранит очередь связей в той же migration.db, что и Profiles/Reservations.
// Дедуп по (Source, Target, Type), чтобы повторные Enqueue не плодили дубли.
public sealed class SqliteRelationshipQueue : IRelationshipQueue
{
    private readonly IMigrationStatistics _stats;
    private readonly string _connectionString;

    public SqliteRelationshipQueue(MigrationOptions options, IMigrationStatistics stats)
    {
        _stats = stats;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.MigrationDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public async Task InitializeSchemaAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS PendingRelationships (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceSrcId  TEXT NOT NULL,
                TargetSrcId  TEXT NOT NULL,
                RelationType TEXT NOT NULL,
                State        INTEGER NOT NULL DEFAULT 0,  -- 0 New, 2 Success, 3 Failed
                Error        TEXT NULL,
                UNIQUE(SourceSrcId, TargetSrcId, RelationType)
            );
            CREATE INDEX IF NOT EXISTS IX_PendingRelationships_State ON PendingRelationships(State);
        ");
    }

    public async Task EnqueueAsync(string sourceSrcId, string targetSrcId, string relationType, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            INSERT INTO PendingRelationships (SourceSrcId, TargetSrcId, RelationType)
            VALUES (@SourceSrcId, @TargetSrcId, @RelationType)
            ON CONFLICT DO NOTHING;";
        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { SourceSrcId = sourceSrcId, TargetSrcId = targetSrcId, RelationType = relationType },
            cancellationToken: ct));
    }

    public async Task EnqueueBatchAsync(IReadOnlyCollection<PendingRelationship> items, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        if (items.Count == 0) return;

        const string sql = @"
            INSERT INTO PendingRelationships (SourceSrcId, TargetSrcId, RelationType)
            VALUES (@SourceSrcId, @TargetSrcId, @RelationType)
            ON CONFLICT DO NOTHING;";

        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, items, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async IAsyncEnumerable<PendingRelationship> DrainPendingAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            SELECT Id, SourceSrcId, TargetSrcId, RelationType
            FROM PendingRelationships
            WHERE State = 0
            ORDER BY Id;";

        await using var conn = await OpenAsync(ct);
        var reader = await conn.ExecuteReaderAsync(new CommandDefinition(sql, cancellationToken: ct));
        var parser = reader.GetRowParser<PendingRelationship>();
        while (await reader.ReadAsync(ct))
            yield return parser(reader);
    }

    public async Task MarkProcessedAsync(long id, bool success, string? error, CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"
            UPDATE PendingRelationships
            SET State = @State, Error = @Error
            WHERE Id = @Id;";

        await using var conn = await OpenAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql,
            new { Id = id, State = success ? 2 : 3, Error = error },
            cancellationToken: ct));
    }

    public async Task<long> GetPendingCountAsync(CancellationToken ct)
    {
        using var _dbScope = _stats.TrackLocalDbOperation();
        const string sql = @"SELECT COUNT(*) FROM PendingRelationships WHERE State = 0;";
        await using var conn = await OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
