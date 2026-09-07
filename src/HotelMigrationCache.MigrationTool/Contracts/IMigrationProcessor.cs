namespace HotelMigrationCache.MigrationTool.Contracts;

public interface IMigrationProcessor
{
    Task InitSourceFilesAsync(CancellationToken ct);
    Task WarmReferenceCacheAsync(CancellationToken ct);
    Task MigrateProfilesAsync(CancellationToken ct);
    Task MigrateRelationshipsAsync(CancellationToken ct);
    Task MigrateReservationsAsync(CancellationToken ct);
    Task SummarizeStatisticsAsync(CancellationToken ct);
}
