using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Contracts;

// Читает XML профайлов и броней из каталогов, парсит в POCO.
public interface ISourceFilesLoader
{
    IAsyncEnumerable<SourceProfile> LoadProfilesAsync(string directory, CancellationToken ct);
    IAsyncEnumerable<SourceReservation> LoadReservationsAsync(string directory, CancellationToken ct);
}
