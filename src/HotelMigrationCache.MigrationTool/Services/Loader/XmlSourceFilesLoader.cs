using System.Runtime.CompilerServices;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Domain;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Loader;

public sealed class XmlSourceFilesLoader : ISourceFilesLoader
{
    private readonly ILogger<XmlSourceFilesLoader> _logger;

    public XmlSourceFilesLoader(ILogger<XmlSourceFilesLoader> logger) => _logger = logger;

    public IAsyncEnumerable<SourceProfile> LoadProfilesAsync(string directory, CancellationToken ct)
        => LoadAsync(directory, SourceXmlParser.ParseProfile, ct);

    public IAsyncEnumerable<SourceReservation> LoadReservationsAsync(string directory, CancellationToken ct)
        => LoadAsync(directory, SourceXmlParser.ParseReservation, ct);

    private async IAsyncEnumerable<T> LoadAsync<T>(
        string directory,
        Func<string, string, T> parse,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Source directory not found: {directory}");

        // OrderBy — детерминированный порядок, полезно для повторяемости.
        var files = Directory.EnumerateFiles(directory, "*.xml").OrderBy(f => f, StringComparer.Ordinal);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            string raw;
            try
            {
                raw = await File.ReadAllTextAsync(file, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read file {File}", file);
                continue;
            }

            T parsed;
            try
            {
                parsed = parse(raw, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse file {File}", file);
                continue;
            }

            yield return parsed;
        }
    }
}
