namespace HotelMigrationCache.MigrationTool.Domain;

public enum MigrationState
{
    New = 0,
    Processing = 1,
    Success = 2,
    Failed = 3,
}
