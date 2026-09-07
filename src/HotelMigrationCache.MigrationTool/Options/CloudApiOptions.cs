namespace HotelMigrationCache.MigrationTool.Options;

public sealed record CloudApiOptions(
    string CloudDbPath,
    int MinLatencyMs,
    int MaxLatencyMs);
