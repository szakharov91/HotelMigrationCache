namespace HotelMigrationCache.MigrationTool.Contracts;

public interface IMigrationTool
{
    Task MigrateAsync(CancellationToken ct);
}
