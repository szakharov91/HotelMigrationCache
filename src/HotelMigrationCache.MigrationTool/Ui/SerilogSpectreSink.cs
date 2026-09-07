using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;
using Spectre.Console;

namespace HotelMigrationCache.MigrationTool.Ui;

/// <summary>
/// Serilog sink, который пишет в консоль через Spectre.Console.
/// AnsiConsole корректно взаимодействует с активной Progress-областью:
/// строки логов появляются над прогресс-баром, сам бар не рвётся.
/// </summary>
public sealed class SerilogSpectreSink : ILogEventSink
{
    private readonly IFormatProvider? _formatProvider;

    public SerilogSpectreSink(IFormatProvider? formatProvider = null) => _formatProvider = formatProvider;

    public void Emit(LogEvent logEvent)
    {
        var (tag, color) = logEvent.Level switch
        {
            LogEventLevel.Verbose     => ("VRB", "grey50"),
            LogEventLevel.Debug       => ("DBG", "grey"),
            LogEventLevel.Information => ("INF", "cyan"),
            LogEventLevel.Warning     => ("WRN", "yellow"),
            LogEventLevel.Error       => ("ERR", "red"),
            LogEventLevel.Fatal       => ("FTL", "bold red"),
            _                         => ("---", "white"),
        };

        var ts = logEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        var msg = logEvent.RenderMessage(_formatProvider);

        AnsiConsole.MarkupLine($"[grey]{ts}[/] [{color}]{tag}[/] {Markup.Escape(msg)}");

        if (logEvent.Exception is not null)
            AnsiConsole.WriteException(logEvent.Exception, ExceptionFormats.ShortenPaths);
    }
}

public static class SerilogSpectreSinkExtensions
{
    // Регистрация в Serilog-конфиге: config.WriteTo.Spectre()
    public static Serilog.LoggerConfiguration Spectre(
        this LoggerSinkConfiguration sinkConfig,
        IFormatProvider? formatProvider = null)
        => sinkConfig.Sink(new SerilogSpectreSink(formatProvider));
}
