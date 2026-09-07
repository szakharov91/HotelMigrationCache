namespace HotelMigrationCache.MigrationTool.Contracts;

// Буфер отложенных связей между профайлами.
// Заполняется в фазе миграции профайлов, разгружается отдельным batch-этапом,
// когда все нужные dstId уже точно созданы.
public interface IRelationshipQueue
{
    Task InitializeSchemaAsync(CancellationToken ct);
    Task EnqueueAsync(string sourceSrcId, string targetSrcId, string relationType, CancellationToken ct);
    Task EnqueueBatchAsync(IReadOnlyCollection<PendingRelationship> items, CancellationToken ct);
    IAsyncEnumerable<PendingRelationship> DrainPendingAsync(CancellationToken ct);
    Task MarkProcessedAsync(long id, bool success, string? error, CancellationToken ct);
    Task<long> GetPendingCountAsync(CancellationToken ct);
}

public sealed record PendingRelationship(long Id, string SourceSrcId, string TargetSrcId, string RelationType);
