using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using HotelMigrationCache.Shared.Otel;

namespace HotelMigrationCache.MigrationTool.Services.Statistics;

// Слушает Meter'ы кэш-сервера (CommandTelemetry) через MeterListener API.
// Работает in-process: если сервер кэша поднят в этом же процессе (--compare mode),
// коллектор увидит все measurement'ы. Если сервер во внешнем процессе — данных не будет
// (для этого пришлось бы поднимать OTEL exporter+collector, что за рамками демо).
public sealed class CacheTelemetryCollector : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<string, CommandStats> _byCommand = new(StringComparer.Ordinal);

    public CacheTelemetryCollector()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CommandTelemetry.ServiceName)
                    listener.EnableMeasurementEvents(instrument);
            },
        };
        _listener.SetMeasurementEventCallback<int>(OnIntMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnDoubleMeasurement);
        _listener.Start();
    }

    private void OnIntMeasurement(Instrument instrument, int value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var name = ExtractCommandName(tags);
        var stats = _byCommand.GetOrAdd(name, _ => new CommandStats());
        Interlocked.Add(ref stats.Count, value);
    }

    private void OnDoubleMeasurement(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        var name = ExtractCommandName(tags);
        var stats = _byCommand.GetOrAdd(name, _ => new CommandStats());
        stats.RecordDuration(value);
    }

    private static string ExtractCommandName(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == CommandTelemetry.Tags.CommandName && tag.Value is string s && !string.IsNullOrEmpty(s))
                return s;
        }
        return "(unknown)";
    }

    public IReadOnlyDictionary<string, CommandStatsSnapshot> Snapshot()
    {
        var result = new Dictionary<string, CommandStatsSnapshot>(StringComparer.Ordinal);
        foreach (var (name, stats) in _byCommand)
            result[name] = stats.ToSnapshot();
        return result;
    }

    public void Dispose() => _listener.Dispose();

    // Внутренняя аккумуляция per-command.
    private sealed class CommandStats
    {
        public long Count;

        private readonly object _lock = new();
        private long _durationSumTicks;
        private long _durationCount;
        private double _minMs = double.MaxValue;
        private double _maxMs;

        public void RecordDuration(double ms)
        {
            lock (_lock)
            {
                _durationSumTicks += TimeSpan.FromMilliseconds(ms).Ticks;
                _durationCount++;
                if (ms < _minMs) _minMs = ms;
                if (ms > _maxMs) _maxMs = ms;
            }
        }

        public CommandStatsSnapshot ToSnapshot()
        {
            lock (_lock)
            {
                var avg = _durationCount == 0 ? 0.0 : (double)_durationSumTicks / _durationCount / TimeSpan.TicksPerMillisecond;
                var min = _durationCount == 0 ? 0.0 : _minMs;
                return new CommandStatsSnapshot(
                    Count: Volatile.Read(ref Count),
                    DurationMeasured: _durationCount,
                    AvgMs: avg,
                    MinMs: min,
                    MaxMs: _maxMs,
                    TotalTime: TimeSpan.FromTicks(_durationSumTicks));
            }
        }
    }
}

public sealed record CommandStatsSnapshot(
    long Count,
    long DurationMeasured,
    double AvgMs,
    double MinMs,
    double MaxMs,
    TimeSpan TotalTime);
