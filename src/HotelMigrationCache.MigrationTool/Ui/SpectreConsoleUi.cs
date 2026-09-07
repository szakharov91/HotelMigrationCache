using System.Globalization;
using HotelMigrationCache.MigrationTool.Contracts;
using HotelMigrationCache.MigrationTool.Services.Statistics;
using HotelMigrationCache.Shared.Common;
using Spectre.Console;

namespace HotelMigrationCache.MigrationTool.Ui;

public sealed class SpectreConsoleUi : IMigrationUi
{
    public void WriteHeader(string title, string subtitle)
    {
        AnsiConsole.Write(new Rule($"[bold yellow]{Markup.Escape(title)}[/]").Centered());
        if (!string.IsNullOrEmpty(subtitle))
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(subtitle)}[/]");
        AnsiConsole.WriteLine();
    }

    public void WriteInfo(string message)
        => AnsiConsole.MarkupLine($"[grey]›[/] {Markup.Escape(message)}");

    public async Task<T> WithProgressAsync<T>(
        string taskDescription,
        int totalUnits,
        Func<IUiProgress, Task<T>> work,
        CancellationToken ct)
    {
        T result = default!;

        await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask(Markup.Escape(taskDescription), maxValue: Math.Max(1, totalUnits));
                var progress = new SpectreUiProgress(task);
                result = await work(progress);
                task.Value = task.MaxValue;
            });

        return result;
    }

    public void RenderStatisticsTable(MigrationStatisticsSnapshot snapshot, string caption)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold yellow]{Markup.Escape(caption)}[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Profiles[/]").Centered())
            .AddColumn(new TableColumn("[bold]Reservations[/]").Centered());

        table.AddRow("Total",
            snapshot.ProfilesTotal.ToString(CultureInfo.InvariantCulture),
            snapshot.ReservationsTotal.ToString(CultureInfo.InvariantCulture));

        table.AddRow("[green]✓ Succeeded[/]",
            $"[green]{snapshot.ProfilesSucceeded}[/]",
            $"[green]{snapshot.ReservationsSucceeded}[/]");

        table.AddRow("[red]✗ Failed[/]",
            $"[red]{snapshot.ProfilesFailed}[/]",
            $"[red]{snapshot.ReservationsFailed}[/]");

        table.AddEmptyRow();

        table.AddRow("Fastest (ms)",
            FormatMs(snapshot.ProfileMin),
            FormatMs(snapshot.ReservationMin));

        table.AddRow("Slowest (ms)",
            FormatMs(snapshot.ProfileMax),
            FormatMs(snapshot.ReservationMax));

        table.AddRow("Average (ms)",
            FormatMs(snapshot.ProfileAverage),
            FormatMs(snapshot.ReservationAverage));

        table.AddRow("Total (serial-eq)",
            $"[bold]{FormatDuration(snapshot.ProfileTotal)}[/]",
            $"[bold]{FormatDuration(snapshot.ReservationTotal)}[/]");
        table.AddRow($"Wall-clock est. (÷{snapshot.MigrationParallelism})",
            $"[bold cyan]{FormatDuration(snapshot.ProfileTotalWallClock)}[/]",
            $"[bold cyan]{FormatDuration(snapshot.ReservationTotalWallClock)}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Overall wall-clock: [bold cyan]{snapshot.Overall.TotalSeconds:F2}s[/]  ·  parallelism: {snapshot.MigrationParallelism}[/]");

        RenderCacheStats(snapshot);
        RenderBackendStats(snapshot);
        RenderApiCostStats(snapshot);
        AnsiConsole.WriteLine();
    }

    private static void RenderApiCostStats(MigrationStatisticsSnapshot snapshot)
    {
        var t = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold yellow]Cloud API cost (vendor rate: ${snapshot.CostPer10kCalls:F0}/10k calls)[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]").RightAligned());

        t.AddRow("Cloud calls (this run)",         $"{snapshot.CloudOperations:N0}");
        t.AddRow("Cost (base)",                    $"[cyan]${snapshot.EstimatedCloudCost:F2}[/]");
        t.AddRow($"Cost with throttling (×{snapshot.ThrottlingOverheadFactor:F2})", $"[cyan]${snapshot.EstimatedCloudCostWithThrottling:F2}[/]");
        t.AddEmptyRow();
        t.AddRow($"Records processed",             $"{snapshot.TotalProcessedRecords:N0}");
        t.AddRow($"Avg cost per record",           $"${snapshot.AvgCostPerRecord:F4}");
        t.AddEmptyRow();
        t.AddRow($"[grey]Projection @ {snapshot.ProjectedRecordCount:N0} records[/]",     "");
        t.AddRow("  Projected cost (base)",        $"[bold cyan]${snapshot.ProjectedCost:F2}[/]");
        t.AddRow("  Projected cost (throttled)",   $"[bold cyan]${snapshot.ProjectedCostWithThrottling:F2}[/]");

        AnsiConsole.Write(t);
    }

    private static void RenderBackendStats(MigrationStatisticsSnapshot snapshot)
    {
        var t = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold yellow]Backend calls (serial-eq · wall-clock ≈ ÷{snapshot.MigrationParallelism})[/]")
            .AddColumn(new TableColumn("[bold]Backend[/]"))
            .AddColumn(new TableColumn("[bold]Ops[/]").Centered())
            .AddColumn(new TableColumn("[bold]Total (serial)[/]").Centered())
            .AddColumn(new TableColumn("[bold]Wall-clock est.[/]").Centered())
            .AddColumn(new TableColumn("[bold]Avg (ms)[/]").Centered());

        // Cache сериализуется SemaphoreSlim(1,1), поэтому wall-clock ≈ total (не делим).
        AddBackendRow(t, "[cyan]Cache[/]",    snapshot.CacheOperations,   snapshot.CacheOperationsTime,   snapshot.CacheOperationsTime);
        AddBackendRow(t, "[yellow]Local DB[/]", snapshot.LocalDbOperations, snapshot.LocalDbOperationsTime, snapshot.LocalDbOperationsWallClock);
        AddBackendRow(t, "[red]Cloud[/]",     snapshot.CloudOperations,   snapshot.CloudOperationsTime,   snapshot.CloudOperationsWallClock);

        AnsiConsole.Write(t);
    }

    private static void AddBackendRow(Table t, string label, int ops, TimeSpan total, TimeSpan wallClock)
    {
        var avg = ops == 0 ? 0.0 : total.TotalMilliseconds / ops;
        t.AddRow(label,
            ops.ToString(CultureInfo.InvariantCulture),
            FormatDuration(total),
            $"[cyan]{FormatDuration(wallClock)}[/]",
            avg.ToString("F1", CultureInfo.InvariantCulture));
    }

    private static void RenderCacheStats(MigrationStatisticsSnapshot snapshot)
    {
        var cacheTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold yellow]Cache activity (reservation phase)[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]").Centered());

        cacheTable.AddRow("Cache hits",         $"[green]{snapshot.CacheHits}[/]");
        cacheTable.AddRow("Cache misses",       $"[yellow]{snapshot.CacheMisses}[/]");
        cacheTable.AddRow("Cloud fallbacks",    $"[red]{snapshot.CloudFallbacks}[/]");
        cacheTable.AddRow("Hit rate",           $"{snapshot.CacheHitRate:P1}");
        cacheTable.AddRow("Cloud calls saved",       $"[bold green]{snapshot.CloudCallsSaved}[/]");
        cacheTable.AddRow("Est. time saved (serial)", $"[bold green]{FormatDuration(snapshot.EstimatedTimeSaved)}[/]");
        cacheTable.AddRow($"Wall-clock saved (÷{snapshot.MigrationParallelism})", $"[bold green]{FormatDuration(snapshot.EstimatedWallClockSaved)}[/]");
        AnsiConsole.Write(cacheTable);

        var refTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold yellow]Reference cache activity[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]").Centered());

        refTable.AddRow("Hits",              $"[green]{snapshot.ReferenceCacheHits}[/]");
        refTable.AddRow("Misses (→ cloud)",  $"[yellow]{snapshot.ReferenceCacheMisses}[/]");
        refTable.AddRow("Hit rate",          $"{snapshot.ReferenceCacheHitRate:P1}");
        refTable.AddRow("Cloud calls saved",       $"[bold green]{snapshot.ReferenceCloudCallsSaved}[/]");
        refTable.AddRow("Est. time saved (serial)", $"[bold green]{FormatDuration(snapshot.ReferenceEstimatedTimeSaved)}[/]");
        refTable.AddRow($"Wall-clock saved (÷{snapshot.MigrationParallelism})", $"[bold green]{FormatDuration(snapshot.ReferenceEstimatedWallClockSaved)}[/]");
        AnsiConsole.Write(refTable);
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0} ms";
        if (ts.TotalMinutes < 1) return $"{ts.TotalSeconds:F1} s";
        return $"{ts.TotalMinutes:F1} min";
    }

    public void RenderComparisonTable(MigrationStatisticsSnapshot noCache, MigrationStatisticsSnapshot withCache)
    {
        var table = new Table()
            .Border(TableBorder.Heavy)
            .Title("[bold yellow]Comparison: cache OFF vs cache ON[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]No cache[/]").Centered())
            .AddColumn(new TableColumn("[bold cyan]With cache[/]").Centered())
            .AddColumn(new TableColumn("[bold]Δ[/]").Centered());

        table.AddRow("Overall (s)",
            $"{noCache.Overall.TotalSeconds:F2}",
            $"{withCache.Overall.TotalSeconds:F2}",
            FormatSpeedup(noCache.Overall, withCache.Overall));

        table.AddEmptyRow();

        table.AddRow("[bold]Profiles[/]", "", "", "");
        table.AddRow("  Succeeded",
            $"[green]{noCache.ProfilesSucceeded}[/]",
            $"[green]{withCache.ProfilesSucceeded}[/]",
            "");
        table.AddRow("  Failed",
            $"[red]{noCache.ProfilesFailed}[/]",
            $"[red]{withCache.ProfilesFailed}[/]",
            "");
        table.AddRow("  Avg (ms)",
            FormatMs(noCache.ProfileAverage),
            FormatMs(withCache.ProfileAverage),
            FormatSpeedup(noCache.ProfileAverage, withCache.ProfileAverage));
        table.AddRow("  Total (serial)",
            FormatDuration(noCache.ProfileTotal),
            FormatDuration(withCache.ProfileTotal),
            FormatSpeedup(noCache.ProfileTotal, withCache.ProfileTotal));
        table.AddRow($"  Wall-clock (÷{withCache.MigrationParallelism})",
            $"[cyan]{FormatDuration(noCache.ProfileTotalWallClock)}[/]",
            $"[cyan]{FormatDuration(withCache.ProfileTotalWallClock)}[/]",
            FormatSpeedup(noCache.ProfileTotalWallClock, withCache.ProfileTotalWallClock));

        table.AddEmptyRow();

        table.AddRow("[bold]Reservations[/]", "", "", "");
        table.AddRow("  Succeeded",
            $"[green]{noCache.ReservationsSucceeded}[/]",
            $"[green]{withCache.ReservationsSucceeded}[/]",
            "");
        table.AddRow("  Failed",
            $"[red]{noCache.ReservationsFailed}[/]",
            $"[red]{withCache.ReservationsFailed}[/]",
            "");
        table.AddRow("  Avg (ms)",
            FormatMs(noCache.ReservationAverage),
            FormatMs(withCache.ReservationAverage),
            FormatSpeedup(noCache.ReservationAverage, withCache.ReservationAverage));
        table.AddRow("  Total (serial)",
            FormatDuration(noCache.ReservationTotal),
            FormatDuration(withCache.ReservationTotal),
            FormatSpeedup(noCache.ReservationTotal, withCache.ReservationTotal));
        table.AddRow($"  Wall-clock (÷{withCache.MigrationParallelism})",
            $"[cyan]{FormatDuration(noCache.ReservationTotalWallClock)}[/]",
            $"[cyan]{FormatDuration(withCache.ReservationTotalWallClock)}[/]",
            FormatSpeedup(noCache.ReservationTotalWallClock, withCache.ReservationTotalWallClock));

        table.AddEmptyRow();

        table.AddRow("[bold]Cache[/]", "", "", "");
        table.AddRow("  Hits",              $"[green]{noCache.CacheHits}[/]",       $"[green]{withCache.CacheHits}[/]",       "");
        table.AddRow("  Misses",            $"[yellow]{noCache.CacheMisses}[/]",    $"[yellow]{withCache.CacheMisses}[/]",    "");
        table.AddRow("  Cloud fallbacks",   $"[red]{noCache.CloudFallbacks}[/]",    $"[red]{withCache.CloudFallbacks}[/]",    "");
        table.AddRow("  Hit rate",          $"{noCache.CacheHitRate:P1}",           $"{withCache.CacheHitRate:P1}",           "");
        table.AddRow("  Calls saved",              $"[green]{noCache.CloudCallsSaved}[/]", $"[bold green]{withCache.CloudCallsSaved}[/]", "");
        table.AddRow("  Time saved (serial)",      FormatDuration(noCache.EstimatedTimeSaved), $"[bold green]{FormatDuration(withCache.EstimatedTimeSaved)}[/]", "");
        table.AddRow($"  Wall-clock saved (÷{withCache.MigrationParallelism})",
            FormatDuration(noCache.EstimatedWallClockSaved),
            $"[bold cyan]{FormatDuration(withCache.EstimatedWallClockSaved)}[/]", "");

        table.AddEmptyRow();

        table.AddRow("[bold]Reference cache[/]", "", "", "");
        table.AddRow("  Hits",              $"[green]{noCache.ReferenceCacheHits}[/]",  $"[green]{withCache.ReferenceCacheHits}[/]",  "");
        table.AddRow("  Misses",            $"[yellow]{noCache.ReferenceCacheMisses}[/]", $"[yellow]{withCache.ReferenceCacheMisses}[/]", "");
        table.AddRow("  Hit rate",          $"{noCache.ReferenceCacheHitRate:P1}",      $"{withCache.ReferenceCacheHitRate:P1}",      "");
        table.AddRow("  Calls saved",              $"[green]{noCache.ReferenceCloudCallsSaved}[/]", $"[bold green]{withCache.ReferenceCloudCallsSaved}[/]", "");
        table.AddRow("  Time saved (serial)",      FormatDuration(noCache.ReferenceEstimatedTimeSaved), $"[bold green]{FormatDuration(withCache.ReferenceEstimatedTimeSaved)}[/]", "");
        table.AddRow($"  Wall-clock saved (÷{withCache.MigrationParallelism})",
            FormatDuration(noCache.ReferenceEstimatedWallClockSaved),
            $"[bold cyan]{FormatDuration(withCache.ReferenceEstimatedWallClockSaved)}[/]", "");

        table.AddEmptyRow();

        table.AddRow($"[bold]Backend time[/] [grey](serial · wall≈÷{withCache.MigrationParallelism})[/]", "", "", "");
        table.AddRow("  Cache ops",
            $"[cyan]{noCache.CacheOperations} · {FormatDuration(noCache.CacheOperationsTime)}[/]",
            $"[cyan]{withCache.CacheOperations} · {FormatDuration(withCache.CacheOperationsTime)}[/]",
            "");
        table.AddRow("  Local DB (serial)",
            $"[yellow]{noCache.LocalDbOperations} · {FormatDuration(noCache.LocalDbOperationsTime)}[/]",
            $"[yellow]{withCache.LocalDbOperations} · {FormatDuration(withCache.LocalDbOperationsTime)}[/]",
            FormatSpeedup(noCache.LocalDbOperationsTime, withCache.LocalDbOperationsTime));
        table.AddRow("  Local DB (wall-clock)",
            $"[cyan]{FormatDuration(noCache.LocalDbOperationsWallClock)}[/]",
            $"[cyan]{FormatDuration(withCache.LocalDbOperationsWallClock)}[/]",
            "");
        table.AddRow("  Cloud (serial)",
            $"[red]{noCache.CloudOperations} · {FormatDuration(noCache.CloudOperationsTime)}[/]",
            $"[red]{withCache.CloudOperations} · {FormatDuration(withCache.CloudOperationsTime)}[/]",
            FormatSpeedup(noCache.CloudOperationsTime, withCache.CloudOperationsTime));
        table.AddRow("  Cloud (wall-clock)",
            $"[bold cyan]{FormatDuration(noCache.CloudOperationsWallClock)}[/]",
            $"[bold cyan]{FormatDuration(withCache.CloudOperationsWallClock)}[/]",
            FormatSpeedup(noCache.CloudOperationsWallClock, withCache.CloudOperationsWallClock));

        table.AddEmptyRow();

        // --- Cloud API cost ---
        var costSavedBase = noCache.EstimatedCloudCost - withCache.EstimatedCloudCost;
        var costSavedThrottled = noCache.EstimatedCloudCostWithThrottling - withCache.EstimatedCloudCostWithThrottling;
        var projectedSavedBase = noCache.ProjectedCost - withCache.ProjectedCost;
        var projectedSavedThrottled = noCache.ProjectedCostWithThrottling - withCache.ProjectedCostWithThrottling;

        table.AddRow($"[bold]Cloud API cost[/] [grey](rate: ${withCache.CostPer10kCalls:F0}/10k)[/]", "", "", "");
        table.AddRow("  Calls (this run)",
            $"{noCache.CloudOperations:N0}",
            $"{withCache.CloudOperations:N0}",
            $"[green]−{noCache.CloudOperations - withCache.CloudOperations:N0}[/]");
        table.AddRow("  Cost — base",
            $"[cyan]${noCache.EstimatedCloudCost:F2}[/]",
            $"[cyan]${withCache.EstimatedCloudCost:F2}[/]",
            $"[bold green]−${costSavedBase:F2}[/]");
        table.AddRow($"  Cost — throttled (×{withCache.ThrottlingOverheadFactor:F2})",
            $"[cyan]${noCache.EstimatedCloudCostWithThrottling:F2}[/]",
            $"[cyan]${withCache.EstimatedCloudCostWithThrottling:F2}[/]",
            $"[bold green]−${costSavedThrottled:F2}[/]");
        table.AddRow($"  [grey]Projection @ {withCache.ProjectedRecordCount:N0} records[/]", "", "", "");
        table.AddRow("    Projected saved (base)",
            "",
            "",
            $"[bold green]${projectedSavedBase:F2}[/]");
        table.AddRow("    Projected saved (throttled)",
            "",
            "",
            $"[bold green]${projectedSavedThrottled:F2}[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string FormatMs(TimeSpan ts)
        => ts.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string FormatSpeedup(TimeSpan slow, TimeSpan fast)
    {
        if (slow.Ticks == 0 || fast.Ticks == 0) return "—";
        var ratio = slow.TotalMilliseconds / fast.TotalMilliseconds;
        return ratio switch
        {
            > 1.05 => $"[green]×{ratio:F2} faster[/]",
            < 0.95 => $"[red]×{1 / ratio:F2} slower[/]",
            _      => "[grey]≈ same[/]",
        };
    }

    public void RenderCacheServerStats(CacheStatistics stats)
    {
        var t = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold yellow]Cache server statistics (from core, STATS command)[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Value[/]").Centered());

        t.AddRow("Keys in store",  $"[cyan]{stats.Count}[/]");
        t.AddRow("Total GETs (hits + misses)", $"{stats.HitCount + stats.MissCount}");
        t.AddRow("  [green]hits[/]",   $"[green]{stats.HitCount}[/]");
        t.AddRow("  [yellow]misses[/]", $"[yellow]{stats.MissCount}[/]");
        var hitRate = stats.HitCount + stats.MissCount == 0
            ? 0.0
            : (double)stats.HitCount / (stats.HitCount + stats.MissCount);
        t.AddRow("  hit rate",     $"{hitRate:P1}");
        t.AddRow("SETs",           $"[cyan]{stats.SetCount}[/]");
        t.AddRow("DELETEs",        $"[cyan]{stats.DeleteCount}[/]");

        AnsiConsole.Write(t);
    }

    public void RenderCacheTelemetry(IReadOnlyDictionary<string, CommandStatsSnapshot> perCommand)
    {
        if (perCommand.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey italic]OpenTelemetry: no measurements collected (cache server not in-process).[/]");
            return;
        }

        var t = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold yellow]OpenTelemetry — Cache server per-command (in-process MeterListener)[/]")
            .AddColumn(new TableColumn("[bold]Command[/]"))
            .AddColumn(new TableColumn("[bold]Count[/]").Centered())
            .AddColumn(new TableColumn("[bold]Total[/]").Centered())
            .AddColumn(new TableColumn("[bold]Avg (ms)[/]").Centered())
            .AddColumn(new TableColumn("[bold]Min (ms)[/]").Centered())
            .AddColumn(new TableColumn("[bold]Max (ms)[/]").Centered());

        foreach (var (name, s) in perCommand.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            t.AddRow(
                Markup.Escape(name),
                s.Count.ToString(CultureInfo.InvariantCulture),
                FormatDuration(s.TotalTime),
                s.AvgMs.ToString("F2", CultureInfo.InvariantCulture),
                s.MinMs.ToString("F2", CultureInfo.InvariantCulture),
                s.MaxMs.ToString("F2", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(t);
    }

    public void RenderJaegerTelemetry(IReadOnlyDictionary<string, JaegerCommandStats> perCommand)
    {
        if (perCommand.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey italic]Jaeger: no traces found for service 'HotelMigrationCache.TcpServer'.[/]");
            return;
        }

        var t = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold yellow]Jaeger telemetry — Cache server per-command (via HTTP API)[/]")
            .AddColumn(new TableColumn("[bold]Command[/]"))
            .AddColumn(new TableColumn("[bold]Count[/]").Centered())
            .AddColumn(new TableColumn("[bold]Total[/]").Centered())
            .AddColumn(new TableColumn("[bold]Avg (ms)[/]").Centered())
            .AddColumn(new TableColumn("[bold]Min (ms)[/]").Centered())
            .AddColumn(new TableColumn("[bold]Max (ms)[/]").Centered());

        foreach (var (name, s) in perCommand.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            t.AddRow(
                Markup.Escape(name),
                s.Count.ToString(CultureInfo.InvariantCulture),
                FormatDuration(s.TotalTime),
                s.AvgMs.ToString("F2", CultureInfo.InvariantCulture),
                s.MinMs.ToString("F2", CultureInfo.InvariantCulture),
                s.MaxMs.ToString("F2", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("[grey italic]Data source: http://localhost:16686 · service: HotelMigrationCache.TcpServer[/]");
    }

    private sealed class SpectreUiProgress : IUiProgress
    {
        private readonly ProgressTask _task;
        public SpectreUiProgress(ProgressTask task) => _task = task;

        public void Advance(int units = 1) => _task.Increment(units);
        public void SetDescription(string description) => _task.Description = Markup.Escape(description);
    }
}
