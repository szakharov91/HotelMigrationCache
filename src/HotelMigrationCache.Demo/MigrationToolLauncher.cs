using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace HotelMigrationCache.Demo;

/// <summary> Лаунчер сервис, поднимает эмуляцию нашей миграционной утилиты для демо </summary>
public sealed class MigrationToolLauncher : BackgroundService
{
    private readonly DemoOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MigrationToolLauncher> _logger;

    public MigrationToolLauncher(
        DemoOptions options,
        IHostApplicationLifetime lifetime,
        ILogger<MigrationToolLauncher> logger)
    {
        _options = options;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ждём, пока кэш-сервер точно начал слушать порт.
        await Task.Delay(750, stoppingToken);

        AnsiConsole.MarkupLine($"[grey]Launching MigrationTool subprocess (--compare --external-cache-server)...[/]");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _options.MigrationToolProject,
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--compare"); // двойной прогон, для демострации сравнения
        psi.ArgumentList.Add("--external-cache-server"); // говорим эмуляции, что кэш внешний

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start MigrationTool process.");

            _logger.LogInformation("MigrationTool started, PID={Pid}", process.Id);

            await process.WaitForExitAsync(stoppingToken);

            _logger.LogInformation("MigrationTool exited with code {ExitCode}", process.ExitCode);
            AnsiConsole.MarkupLine($"[grey]MigrationTool subprocess finished (exit {process.ExitCode}). Flushing OTEL and stopping host...[/]");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MigrationTool subprocess failed.");
        }
        finally
        {
            // Даём OTEL SDK время сбросить накопленное в коллектор перед выходом.
            await Task.Delay(2000, CancellationToken.None);
            _lifetime.StopApplication();
        }
    }
}
