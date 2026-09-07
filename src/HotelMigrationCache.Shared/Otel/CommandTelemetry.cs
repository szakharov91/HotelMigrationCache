using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HotelMigrationCache.Shared.Otel;

public static class CommandTelemetry
{
    public static readonly string ServiceName = "HotelMigrationCache.TcpServer";
    public static readonly ActivitySource ActivitySource = new ActivitySource(ServiceName);
    public static readonly Meter Meter = new Meter(ServiceName);

    public static readonly Counter<int> CommandsProcessedCounter =
        Meter.CreateCounter<int>("commands.processed", "Number of commands processed.");
    public static readonly Histogram<double> CommandsDurationHistogram =
        Meter.CreateHistogram<double>("commands.duration_ms", unit: "ms", description: "Commands execution time in milliseconds.");

    public static class Tags
    {
        public static readonly string CommandName = "command.name";
        public static readonly string ResponseStatus = "response.status";
        public static readonly string PayloadSize = "payload.size";
        public static readonly string ClientEndpoint = "client.endpoint";
    }

    public static class Activities
    {
        public static readonly string CommandProcessing = "CommandProcessing";
    }
}
