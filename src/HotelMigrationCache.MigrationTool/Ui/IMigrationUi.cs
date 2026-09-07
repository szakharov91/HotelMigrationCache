using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Services.Statistics;
using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.MigrationTool.Ui;

// Абстракция над Spectre.Console; сервисы вызывают через неё,
// а не работают с AnsiConsole напрямую — это делает миграцию тестируемой.
public interface IMigrationUi
{
    void WriteHeader(string title, string subtitle);
    void WriteInfo(string message);

    Task<T> WithProgressAsync<T>(string taskDescription, int totalUnits, Func<IUiProgress, Task<T>> work, CancellationToken ct);

    void RenderStatisticsTable(MigrationStatisticsSnapshot snapshot, string caption);

    // Двухколоночная таблица для режима --compare.
    void RenderComparisonTable(MigrationStatisticsSnapshot noCache, MigrationStatisticsSnapshot withCache);

    // Реальная статистика от ядра кэша (STATS command).
    void RenderCacheServerStats(CacheStatistics stats);

    // Агрегированные OpenTelemetry-метрики сервера кэша (in-process MeterListener).
    void RenderCacheTelemetry(IReadOnlyDictionary<string, CommandStatsSnapshot> perCommand);

    // Данные из Jaeger REST API (когда cache-сервер в отдельном процессе, а мы читаем trace'ы через сеть).
    void RenderJaegerTelemetry(IReadOnlyDictionary<string, JaegerCommandStats> perCommand);
}

public interface IUiProgress
{
    void Advance(int units = 1);
    void SetDescription(string description);
}
