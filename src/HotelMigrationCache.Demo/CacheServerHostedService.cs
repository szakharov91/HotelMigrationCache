using System.Net;
using HotelMigrationCache.Core.Interfaces;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelMigrationCache.Demo;

/// <summary> Сервис для раскрутки tcp интерфейса нашего кэша </summary>
public sealed class CacheServerHostedService : BackgroundService
{
    private readonly int _port;
    private readonly ILogger<CacheServerHostedService> _logger;

    private IKeyValueStore? _store;
    private IProtocolInterface? _protocol;

    public CacheServerHostedService(DemoOptions options, ILogger<CacheServerHostedService> logger)
    {
        _port = options.CachePort;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _store = new InMemoryKeyValueStore();
        _protocol = new TcpServerInterface(_store, _port, IPAddress.Any);

        _logger.LogInformation("Cache TCP server listening on {Port}", _port);

        try
        {
            await _protocol.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "Cache server stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cache server crashed.");
            throw;
        }
    }

    public override void Dispose()
    {
        _protocol?.Dispose();
        _store?.Dispose();
        base.Dispose();
    }
}

public sealed record DemoOptions(int CachePort, string MigrationToolProject, string OtlpEndpoint);
