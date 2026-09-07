using HotelMigrationCache.Shared.Otel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Spectre.Console;

namespace HotelMigrationCache.Demo;

// Demo — оркестратор:
//   1. Настраивает OpenTelemetry SDK (traces + metrics) для ActivitySource и Meter из CommandTelemetry.
//   2. Поднимает cache TCP server (BackgroundService) — телеметрия сервера ловится SDK этого же процесса.
//   3. Стартует subprocess MigrationTool с `--compare --external-cache-server`.
//   4. Ждёт завершения subprocess, флашит OTEL и останавливается.
//
// Требования: docker-compose up (otel-collector + jaeger) должны быть подняты параллельно.
public static class Program
{
    public static async Task Main(string[] args)
    {
        AnsiConsole.Write(new Rule("[bold yellow]HotelMigrationCache · Demo orchestrator[/]").Centered());

        if (args.Contains("--bench"))
        {
            AnsiConsole.Write(new Rule("[bold yellow]BENCHMARK -> RUN ONLY CacheServerHostedService[/]").Centered());
        }

        AnsiConsole.MarkupLine("[grey italic]OTLP endpoint: http://localhost:4317 · Jaeger UI: http://localhost:16686[/]");
        AnsiConsole.WriteLine();

        var solutionRoot = FindSolutionRoot();
        var migrationToolProject = Path.Combine(solutionRoot, "src", "HotelMigrationCache.MigrationTool");

        var demoOptions = new DemoOptions(
            CachePort: 3456,
            MigrationToolProject: migrationToolProject,
            OtlpEndpoint: "http://localhost:4317");

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddSingleton(demoOptions);

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(CommandTelemetry.ServiceName, serviceVersion: "1.0.0"))
            .WithTracing(t => t
                .AddSource(CommandTelemetry.ServiceName)
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = new Uri(demoOptions.OtlpEndpoint);
                    o.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                }))
            .WithMetrics(m => m
                .AddMeter(CommandTelemetry.ServiceName)
                .AddOtlpExporter((exporter, reader) =>
                {
                    exporter.Endpoint = new Uri(demoOptions.OtlpEndpoint);
                    exporter.Protocol = OpenTelemetry.Exporter.OtlpExportProtocol.Grpc;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 2000;
                }));

        // Порядок регистрации HostedService важен: кэш-сервер стартует первым,
        // потом лончер MigrationTool. Внутри лончера есть небольшая задержка для гарантии.
        builder.Services.AddHostedService<CacheServerHostedService>();

        if (!args.Contains("--bench"))
        {
            builder.Services.AddHostedService<MigrationToolLauncher>();
        }

        builder.Logging.SetMinimumLevel(LogLevel.Information);

        var host = builder.Build();
        await host.RunAsync();

        AnsiConsole.MarkupLine("[grey]Demo finished.[/]");
    }

    private static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && dir.GetFiles("*.slnx").Length == 0 && dir.GetFiles("*.sln").Length == 0)
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
