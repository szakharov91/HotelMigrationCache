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

        // === 1. Headline: сколько времени всё заняло ===
        table.AddRow("[bold]⏱  How long the whole migration took[/]",
            $"[cyan]{FormatDurationLong(noCache.Overall)}[/]",
            $"[bold cyan]{FormatDurationLong(withCache.Overall)}[/]",
            FormatSpeedup(noCache.Overall, withCache.Overall));

        table.AddEmptyRow();

        // === 2. Скорость на одну запись ===
        table.AddRow("[bold]📦 How long one record took on average[/]", "", "", "");
        table.AddRow($"  Profile ([grey]{withCache.ProfilesSucceeded} records[/])",
            FormatMsHuman(noCache.ProfileAverage),
            FormatMsHuman(withCache.ProfileAverage),
            FormatSpeedup(noCache.ProfileAverage, withCache.ProfileAverage));
        table.AddRow($"  Reservation ([grey]{withCache.ReservationsSucceeded} records[/])",
            FormatMsHuman(noCache.ReservationAverage),
            FormatMsHuman(withCache.ReservationAverage),
            FormatSpeedup(noCache.ReservationAverage, withCache.ReservationAverage));

        table.AddEmptyRow();

        // === 3. Что сделал кэш — объединяем profile + reference (это один и тот же TCP-кэш) ===
        int totalHits = noCache.CacheHits + noCache.ReferenceCacheHits
                        + withCache.CacheHits + withCache.ReferenceCacheHits;
        _ = totalHits; // suppress unused
        int noLookups = noCache.CacheHits + noCache.CacheMisses
                        + noCache.ReferenceCacheHits + noCache.ReferenceCacheMisses;
        int noHits = noCache.CacheHits + noCache.ReferenceCacheHits;
        int noMisses = noCache.CacheMisses + noCache.ReferenceCacheMisses;
        int wcLookups = withCache.CacheHits + withCache.CacheMisses
                        + withCache.ReferenceCacheHits + withCache.ReferenceCacheMisses;
        int wcHits = withCache.CacheHits + withCache.ReferenceCacheHits;
        int wcMisses = withCache.CacheMisses + withCache.ReferenceCacheMisses;
        double noHitRate = noLookups == 0 ? 0 : (double)noHits / noLookups;
        double wcHitRate = wcLookups == 0 ? 0 : (double)wcHits / wcLookups;

        table.AddRow("[bold]💾 What the cache did[/]", "", "", "");
        table.AddRow("  Look-ups (profiles + reference data)",
            $"[grey]{noLookups:N0}[/]",
            $"[grey]{wcLookups:N0}[/]",
            "");
        table.AddRow("  Answered from cache [grey](instant)[/]",
            $"[green]{noHits:N0}[/]",
            $"[bold green]{wcHits:N0}[/]",
            wcHits > noHits ? $"[green]+{wcHits - noHits:N0}[/]" : "");
        table.AddRow("  Had to ask cloud [grey](slow)[/]",
            $"[red]{noMisses:N0}[/]",
            $"[red]{wcMisses:N0}[/]",
            noMisses > wcMisses ? $"[green]−{noMisses - wcMisses:N0}[/]" : "");
        table.AddRow("  Hit rate",
            $"[grey]{noHitRate:P1}[/]",
            $"[bold green]{wcHitRate:P1}[/]",
            "");

        table.AddEmptyRow();

        // === 4. Cloud — что реально долгое ===
        table.AddRow("[bold]☁  Time spent talking to the cloud[/]", "", "", "");
        table.AddRow("  Cloud calls (total)",
            $"[red]{noCache.CloudOperations:N0}[/]",
            $"[cyan]{withCache.CloudOperations:N0}[/]",
            $"[green]−{noCache.CloudOperations - withCache.CloudOperations:N0}[/]");
        table.AddRow($"  Wall-clock waiting for cloud [grey](parallelism ÷{withCache.MigrationParallelism})[/]",
            $"[red]{FormatDurationLong(noCache.CloudOperationsWallClock)}[/]",
            $"[bold cyan]{FormatDurationLong(withCache.CloudOperationsWallClock)}[/]",
            FormatSpeedup(noCache.CloudOperationsWallClock, withCache.CloudOperationsWallClock));

        table.AddEmptyRow();

        // === 5. Deньги за этот прогон ===
        var costSavedBase = noCache.EstimatedCloudCost - withCache.EstimatedCloudCost;
        var costSavedThrottled = noCache.EstimatedCloudCostWithThrottling - withCache.EstimatedCloudCostWithThrottling;

        table.AddRow($"[bold]💰 Money spent on this run[/] [grey](vendor rate: ${withCache.CostPer10kCalls:F0} per 10 000 calls)[/]", "", "", "");
        table.AddRow("  Best case [grey](if every call succeeds)[/]",
            $"[cyan]${noCache.EstimatedCloudCost:F2}[/]",
            $"[cyan]${withCache.EstimatedCloudCost:F2}[/]",
            $"[bold green]−${costSavedBase:F2}[/]");
        table.AddRow($"  Realistic [grey](×{withCache.ThrottlingOverheadFactor:F2} for retries on rate-limits)[/]",
            $"[cyan]${noCache.EstimatedCloudCostWithThrottling:F2}[/]",
            $"[cyan]${withCache.EstimatedCloudCostWithThrottling:F2}[/]",
            $"[bold green]−${costSavedThrottled:F2}[/]");

        table.AddEmptyRow();

        // === 6. Проекция на реальный отель — крупным ===
        var projectedSavedBase = noCache.ProjectedCost - withCache.ProjectedCost;
        var projectedSavedThrottled = noCache.ProjectedCostWithThrottling - withCache.ProjectedCostWithThrottling;

        table.AddRow($"[bold]🏨 Projected saving on a real hotel migration[/] [grey](same rate applied to {withCache.ProjectedRecordCount:N0} records)[/]", "", "", "");
        table.AddRow("  Best case",
            "",
            "",
            $"[bold green]${projectedSavedBase:F2} saved[/]");
        table.AddRow("  Realistic (with retries)",
            "",
            "",
            $"[bold green]${projectedSavedThrottled:F2} saved[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static string FormatDurationLong(TimeSpan ts)
    {
        if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0} ms";
        if (ts.TotalMinutes < 1) return $"{ts.TotalSeconds:F1} s";
        if (ts.TotalHours < 1)
        {
            int min = (int)ts.TotalMinutes;
            int sec = (int)Math.Round((ts.TotalMinutes - min) * 60);
            if (sec == 60) { min += 1; sec = 0; }
            return sec == 0 ? $"{min} min" : $"{min} min {sec} s";
        }
        int h = (int)ts.TotalHours;
        int m = (int)Math.Round((ts.TotalHours - h) * 60);
        if (m == 60) { h += 1; m = 0; }
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    private static string FormatMsHuman(TimeSpan ts)
    {
        if (ts.TotalMilliseconds < 1) return "0 ms";
        if (ts.TotalSeconds < 1) return $"{ts.TotalMilliseconds:F0} ms";
        return $"{ts.TotalSeconds:F1} s";
    }

    public void RenderComparisonTable3(
        MigrationStatisticsSnapshot noCache,
        MigrationStatisticsSnapshot cacheStandard,
        MigrationStatisticsSnapshot cacheBoosted)
    {
        var table = new Table()
            .Border(TableBorder.Heavy)
            .Title("[bold yellow]Comparison: 3 scenarios (Oracle 50-rps budget context)[/]")
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn($"[bold](1) No cache · p={noCache.MigrationParallelism}[/]").Centered())
            .AddColumn(new TableColumn($"[bold cyan](2) Cache · p={cacheStandard.MigrationParallelism}[/]").Centered())
            .AddColumn(new TableColumn($"[bold green](3) Cache · p={cacheBoosted.MigrationParallelism} (boosted)[/]").Centered());

        // === 1. Wall-clock ===
        table.AddRow("[bold]⏱  Whole migration wall-clock[/]",
            $"[cyan]{FormatDurationLong(noCache.Overall)}[/]",
            $"[bold cyan]{FormatDurationLong(cacheStandard.Overall)}[/] [grey]{SpeedupLabel(noCache.Overall, cacheStandard.Overall)}[/]",
            $"[bold green]{FormatDurationLong(cacheBoosted.Overall)}[/] [grey]{SpeedupLabel(noCache.Overall, cacheBoosted.Overall)}[/]");

        table.AddEmptyRow();

        // === 2. Avg per record ===
        table.AddRow("[bold]📦 One record on average[/]", "", "", "");
        table.AddRow($"  Profile ([grey]{cacheStandard.ProfilesSucceeded} records[/])",
            FormatMsHuman(noCache.ProfileAverage),
            FormatMsHuman(cacheStandard.ProfileAverage),
            FormatMsHuman(cacheBoosted.ProfileAverage));
        table.AddRow($"  Reservation ([grey]{cacheStandard.ReservationsSucceeded} records[/])",
            FormatMsHuman(noCache.ReservationAverage),
            FormatMsHuman(cacheStandard.ReservationAverage),
            FormatMsHuman(cacheBoosted.ReservationAverage));

        table.AddEmptyRow();

        // === 3. Cache activity ===
        int noLookups   = noCache.CacheHits + noCache.CacheMisses + noCache.ReferenceCacheHits + noCache.ReferenceCacheMisses;
        int noHits      = noCache.CacheHits + noCache.ReferenceCacheHits;
        int stdLookups  = cacheStandard.CacheHits + cacheStandard.CacheMisses + cacheStandard.ReferenceCacheHits + cacheStandard.ReferenceCacheMisses;
        int stdHits     = cacheStandard.CacheHits + cacheStandard.ReferenceCacheHits;
        int bstLookups  = cacheBoosted.CacheHits + cacheBoosted.CacheMisses + cacheBoosted.ReferenceCacheHits + cacheBoosted.ReferenceCacheMisses;
        int bstHits     = cacheBoosted.CacheHits + cacheBoosted.ReferenceCacheHits;

        double noHitRate  = noLookups  == 0 ? 0 : (double)noHits  / noLookups;
        double stdHitRate = stdLookups == 0 ? 0 : (double)stdHits / stdLookups;
        double bstHitRate = bstLookups == 0 ? 0 : (double)bstHits / bstLookups;

        table.AddRow("[bold]💾 What the cache did[/]", "", "", "");
        table.AddRow("  Look-ups (total)",
            $"[grey]{noLookups:N0}[/]",
            $"[grey]{stdLookups:N0}[/]",
            $"[grey]{bstLookups:N0}[/]");
        table.AddRow("  Answered from cache",
            $"[grey]{noHits:N0}[/]",
            $"[bold green]{stdHits:N0}[/]",
            $"[bold green]{bstHits:N0}[/]");
        table.AddRow("  Hit rate",
            $"[grey]{noHitRate:P1}[/]",
            $"[bold green]{stdHitRate:P1}[/]",
            $"[bold green]{bstHitRate:P1}[/]");

        table.AddEmptyRow();

        // === 4. Cloud pressure ===
        // Per-worker rate — сколько cloud calls воркер выдаёт в среднем в секунду при своём parallelism.
        double noRps  = noCache.CloudOperationsWallClock.Ticks  <= 0 ? 0 : noCache.CloudOperations  / noCache.CloudOperationsWallClock.TotalSeconds;
        double stdRps = cacheStandard.CloudOperationsWallClock.Ticks <= 0 ? 0 : cacheStandard.CloudOperations / cacheStandard.CloudOperationsWallClock.TotalSeconds;
        double bstRps = cacheBoosted.CloudOperationsWallClock.Ticks  <= 0 ? 0 : cacheBoosted.CloudOperations  / cacheBoosted.CloudOperationsWallClock.TotalSeconds;
        const double oracleSustainedRps = 50.0;

        table.AddRow("[bold]☁  Cloud pressure (vs Oracle 50 rps limit)[/]", "", "", "");
        table.AddRow("  Cloud calls (total)",
            $"[red]{noCache.CloudOperations:N0}[/]",
            $"[cyan]{cacheStandard.CloudOperations:N0}[/]",
            $"[cyan]{cacheBoosted.CloudOperations:N0}[/]");
        table.AddRow("  Wall-clock waiting for cloud",
            $"[red]{FormatDurationLong(noCache.CloudOperationsWallClock)}[/]",
            $"[bold cyan]{FormatDurationLong(cacheStandard.CloudOperationsWallClock)}[/]",
            $"[bold green]{FormatDurationLong(cacheBoosted.CloudOperationsWallClock)}[/]");
        table.AddRow("  Cloud rps (effective)",
            RpsCell(noRps, oracleSustainedRps),
            RpsCell(stdRps, oracleSustainedRps),
            RpsCell(bstRps, oracleSustainedRps));

        table.AddEmptyRow();

        // === 5. Money ===
        var costSavedStd = noCache.EstimatedCloudCostWithThrottling - cacheStandard.EstimatedCloudCostWithThrottling;
        var costSavedBst = noCache.EstimatedCloudCostWithThrottling - cacheBoosted.EstimatedCloudCostWithThrottling;

        table.AddRow($"[bold]💰 Money on this run[/] [grey](rate: ${cacheStandard.CostPer10kCalls:F0} / 10k · ×{cacheStandard.ThrottlingOverheadFactor:F2} realistic)[/]", "", "", "");
        table.AddRow("  Best case",
            $"[cyan]${noCache.EstimatedCloudCost:F2}[/]",
            $"[cyan]${cacheStandard.EstimatedCloudCost:F2}[/]",
            $"[cyan]${cacheBoosted.EstimatedCloudCost:F2}[/]");
        table.AddRow("  Realistic",
            $"[cyan]${noCache.EstimatedCloudCostWithThrottling:F2}[/]",
            $"[cyan]${cacheStandard.EstimatedCloudCostWithThrottling:F2}[/] [green]−${costSavedStd:F2}[/]",
            $"[cyan]${cacheBoosted.EstimatedCloudCostWithThrottling:F2}[/] [green]−${costSavedBst:F2}[/]");

        table.AddEmptyRow();

        // === 6. Projection ===
        var projSavedStd = noCache.ProjectedCostWithThrottling - cacheStandard.ProjectedCostWithThrottling;
        var projSavedBst = noCache.ProjectedCostWithThrottling - cacheBoosted.ProjectedCostWithThrottling;

        table.AddRow($"[bold]🏨 Projected saving on {cacheStandard.ProjectedRecordCount:N0} records (realistic)[/]",
            "[grey]—[/]",
            $"[bold green]${projSavedStd:F2} saved[/]",
            $"[bold green]${projSavedBst:F2} saved[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Резюме в 3 строки под таблицей
        var boostVsStdRatio = cacheStandard.Overall.Ticks <= 0 || cacheBoosted.Overall.Ticks <= 0
            ? 0 : cacheStandard.Overall.TotalMilliseconds / cacheBoosted.Overall.TotalMilliseconds;
        var totalRatio = noCache.Overall.Ticks <= 0 || cacheBoosted.Overall.Ticks <= 0
            ? 0 : noCache.Overall.TotalMilliseconds / cacheBoosted.Overall.TotalMilliseconds;
        var cacheOnlyRatio = noCache.Overall.Ticks <= 0 || cacheStandard.Overall.Ticks <= 0
            ? 0 : noCache.Overall.TotalMilliseconds / cacheStandard.Overall.TotalMilliseconds;

        AnsiConsole.MarkupLine($"[bold]Cache contribution[/] (1 → 2, at same parallelism): [green]×{cacheOnlyRatio:F2} faster[/]");
        AnsiConsole.MarkupLine($"[bold]Safe-boost contribution[/] (2 → 3, cache enables higher parallelism): [green]×{boostVsStdRatio:F2} faster[/]");
        AnsiConsole.MarkupLine($"[bold]Total[/] (1 → 3, cache + boosted parallelism): [bold green]×{totalRatio:F2} faster[/]");
        AnsiConsole.WriteLine();
    }

    private static string RpsCell(double rps, double sustainedLimit)
    {
        var pct = rps / sustainedLimit * 100;
        var color = pct switch
        {
            < 40 => "green",
            < 80 => "yellow",
            _    => "red",
        };
        return $"[{color}]{rps:F1} rps ({pct:F0}% of {sustainedLimit:F0})[/]";
    }

    private static string SpeedupLabel(TimeSpan slow, TimeSpan fast)
    {
        if (slow.Ticks == 0 || fast.Ticks == 0) return "";
        var ratio = slow.TotalMilliseconds / fast.TotalMilliseconds;
        return ratio > 1.05 ? $"×{ratio:F2} vs (1)" : "";
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
