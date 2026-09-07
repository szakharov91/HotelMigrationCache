namespace HotelMigrationCache.MigrationTool.Domain;

// Строка таблицы Reservations в локальной SQLite базе миграции.
public sealed class ReservationRecord
{
    public long Id { get; set; }
    public string SourceFilename { get; set; } = string.Empty;
    public string FileRawContent { get; set; } = string.Empty;
    public string SrcId { get; set; } = string.Empty;
    public string? DstId { get; set; }
    public MigrationState State { get; set; } = MigrationState.New;
    public string? Error { get; set; }
    public string MainProfileSrcId { get; set; } = string.Empty;
    public string? MainProfileDstId { get; set; }
}
