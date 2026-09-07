using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Options;

public sealed record MigrationOptions(
    string SourceProfilesDirectory,
    string SourceBookingsDirectory,
    string MigrationDbPath,
    bool UseCacheService,
    // Демонстрационные лимиты. 0 = без ограничения (полный прогон).
    int MaxProfilesToMigrate,
    int MaxReservationsToMigrate,
    MigrationConcurrency Concurrency = MigrationConcurrency.One);
