using HotelMigrationCache.MigrationTool.Contracts;

namespace HotelMigrationCache.MigrationTool.Services;

// Прогоняет 4 шага миграции по порядку. Тонкий обёрточный слой над IMigrationProcessor,
// чтобы Main/BackgroundService не знал про последовательность шагов.
public sealed class MigrationTool : IMigrationTool
{
    private readonly IMigrationProcessor _processor;

    public MigrationTool(IMigrationProcessor processor) => _processor = processor;

    public async Task MigrateAsync(CancellationToken ct)
    {
        await _processor.InitSourceFilesAsync(ct);
        await _processor.WarmReferenceCacheAsync(ct);
        await _processor.MigrateProfilesAsync(ct);
        await _processor.MigrateRelationshipsAsync(ct);
        await _processor.MigrateReservationsAsync(ct);
        await _processor.SummarizeStatisticsAsync(ct);
    }
}
