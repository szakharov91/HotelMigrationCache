namespace HotelMigrationCache.MigrationTool.Domain;

// Строка таблицы Profiles в локальной SQLite базе миграции.
public sealed class ProfileRecord
{
    public long Id { get; set; }
    public string SourceFilename { get; set; } = string.Empty;
    public string FileRawContent { get; set; } = string.Empty;
    public string SrcId { get; set; } = string.Empty;
    public string? DstId { get; set; }
    public MigrationState State { get; set; } = MigrationState.New;
    public string? Error { get; set; }
}
