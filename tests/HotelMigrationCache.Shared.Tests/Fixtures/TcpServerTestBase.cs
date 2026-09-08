using System.Net;
using System.Net.Sockets;
using HotelMigrationCache.Core.Interfaces;
using HotelMigrationCache.Core.Store;

namespace HotelMigrationCache.Shared.Tests.Fixtures;

/// <summary>
/// Базовый класс интеграционных тестов: поднимает свежий <see cref="TcpServerInterface"/>
/// на эфемерном loopback-порту перед каждым тестом. xUnit создаёт новый экземпляр
/// тестового класса на каждый тест → полная изоляция состояния хранилища.
/// </summary>
public abstract class TcpServerTestBase : IAsyncLifetime
{
    protected InMemoryKeyValueStore Store { get; private set; } = null!;
    protected int Port { get; private set; }

    private TcpServerInterface _server = null!;
    private CancellationTokenSource _serverCts = null!;
    private Task _serverTask = null!;

    public async Task InitializeAsync()
    {
        Port = PickFreePort();
        Store = new InMemoryKeyValueStore();
        _server = new TcpServerInterface(Store, Port, IPAddress.Loopback);
        _serverCts = new CancellationTokenSource();

        _serverTask = Task.Run(() => _server.RunAsync(_serverCts.Token));

        // Дать серверу время связать сокет и войти в accept-loop.
        // Локально — миллисекунды; берём с запасом, чтобы не флапать.
        await Task.Delay(150);
    }

    public async Task DisposeAsync()
    {
        try { await _serverCts.CancelAsync(); } catch { /* уже отменён */ }
        _server.Dispose();
        try { await _serverTask; } catch { /* accept-loop бросает при закрытии сокета — ожидаемо */ }
        _serverCts.Dispose();
    }

    /// <summary>Просим ОС выдать свободный порт (bind на 0), сразу отпускаем — гонка минимальна.</summary>
    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
