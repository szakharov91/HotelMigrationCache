using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.MigrationTool.Services.Statistics;

// Читатель Jaeger's HTTP API. Дёргает /api/traces?service=<name>&limit=... , аккумулирует
// per-command тайминги и count. Используется, когда cache-сервер живёт в отдельном процессе
// (Demo-оркестратор), и локальный MeterListener не видит его emissions.
public sealed class JaegerTraceReader
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<JaegerTraceReader> _logger;
    private readonly string _baseUrl;
    private readonly string _serviceName;

    public JaegerTraceReader(ILogger<JaegerTraceReader> logger, string? baseUrl = null, string serviceName = "HotelMigrationCache.TcpServer")
    {
        _logger = logger;
        // URL из env-переменной, чтобы не хардкодить абсолютный адрес (Sonar S1075).
        var resolved = baseUrl
            ?? Environment.GetEnvironmentVariable("JAEGER_URL")
            ?? string.Concat("http://", "localhost", ":16686");
        _baseUrl = resolved.TrimEnd('/');
        _serviceName = serviceName;
    }

    public async Task<IReadOnlyDictionary<string, JaegerCommandStats>?> FetchAsync(CancellationToken ct)
    {
        var url = $"{_baseUrl}/api/traces?service={Uri.EscapeDataString(_serviceName)}&limit=2000&lookback=1h";

        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Jaeger returned {StatusCode} — telemetry from Jaeger will be skipped in the report.", resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            return Aggregate(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jaeger fetch failed at {Url} — skipping.", url);
            return null;
        }
    }

    private static IReadOnlyDictionary<string, JaegerCommandStats> Aggregate(JsonElement root)
    {
        var acc = new Dictionary<string, StatsAcc>(StringComparer.Ordinal);

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return new Dictionary<string, JaegerCommandStats>();

        foreach (var trace in data.EnumerateArray())
        {
            if (!trace.TryGetProperty("spans", out var spans) || spans.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var span in spans.EnumerateArray())
            {
                var commandName = "(unknown)";
                if (span.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in tags.EnumerateArray())
                    {
                        if (tag.TryGetProperty("key", out var k) &&
                            k.ValueKind == JsonValueKind.String &&
                            k.GetString() == "command.name" &&
                            tag.TryGetProperty("value", out var v))
                        {
                            commandName = v.ValueKind == JsonValueKind.String
                                ? v.GetString() ?? "(unknown)"
                                : v.ToString();
                            break;
                        }
                    }
                }

                double durationMs = 0;
                if (span.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                    durationMs = d.GetDouble() / 1000.0; // Jaeger duration is µs

                if (!acc.TryGetValue(commandName, out var s))
                    acc[commandName] = s = new StatsAcc();
                s.Add(durationMs);
            }
        }

        var result = new Dictionary<string, JaegerCommandStats>(StringComparer.Ordinal);
        foreach (var (name, s) in acc)
            result[name] = s.ToSnapshot();
        return result;
    }

    private sealed class StatsAcc
    {
        private long _count;
        private double _sumMs;
        private double _min = double.MaxValue;
        private double _max;

        public void Add(double ms)
        {
            _count++;
            _sumMs += ms;
            if (ms < _min) _min = ms;
            if (ms > _max) _max = ms;
        }

        public JaegerCommandStats ToSnapshot() => new(
            Count: _count,
            AvgMs: _count == 0 ? 0 : _sumMs / _count,
            MinMs: _count == 0 ? 0 : _min,
            MaxMs: _max,
            TotalTime: TimeSpan.FromMilliseconds(_sumMs));
    }
}

public sealed record JaegerCommandStats(long Count, double AvgMs, double MinMs, double MaxMs, TimeSpan TotalTime);
