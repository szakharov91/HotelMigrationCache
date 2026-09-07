using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Contracts;

public interface IProfileMigrator
{
    // Мигрирует одну строку из локальной таблицы Profiles. Возвращает облачный dstId при успехе.
    Task<string?> MigrateAsync(ProfileRecord record, CancellationToken ct);
}
