using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Contracts;

public interface IReservationMigrator
{
    Task<string?> MigrateAsync(ReservationRecord record, CancellationToken ct);
}
