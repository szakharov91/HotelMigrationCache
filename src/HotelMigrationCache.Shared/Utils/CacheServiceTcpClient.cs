using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Protocol;

namespace HotelMigrationCache.Shared.Utils;

/// <summary>
/// Клиент к TCP-кэшу с пулом из N постоянных соединений (1..<see cref="ServerLimits.MaxServerConnections"/>).
/// Каждый запрос выбирается по round-robin в свободное соединение из пула;
/// внутри одного соединения запись→чтение сериализуется семафором (иначе поплывёт framing).
/// </summary>
public sealed class CacheServiceTcpClient : ICacheServiceClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _bufferSize = 1024 * 1024;

    private readonly TcpClient?[] _clients;
    private readonly SemaphoreSlim[] _connectLocks;
    private readonly SemaphoreSlim[] _ioLocks;
    private int _rrCounter;
    private bool _disposedValue;

    /// <param name="host">Хост сервера.</param>
    /// <param name="port">Порт сервера.</param>
    /// <param name="poolSize">
    /// Размер пула TCP-соединений. Должен быть в диапазоне 1..<see cref="ServerLimits.MaxServerConnections"/>.
    /// Значение выше - превышение серверного семафора одновременных клиентов.
    /// </param>
    public CacheServiceTcpClient(string host, int port, int poolSize = 1)
    {
        if (poolSize < 1 || poolSize > ServerLimits.MaxServerConnections)
            throw new ArgumentOutOfRangeException(nameof(poolSize), poolSize,
                $"Pool size must be in [1..{ServerLimits.MaxServerConnections}].");

        _host = host;
        _port = port;
        _clients = new TcpClient?[poolSize];
        _connectLocks = new SemaphoreSlim[poolSize];
        _ioLocks = new SemaphoreSlim[poolSize];
        for (int i = 0; i < poolSize; i++)
        {
            _connectLocks[i] = new SemaphoreSlim(1, 1);
            _ioLocks[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async Task<CacheServiceResponse> GetAsync(string key)
    {
        var idx = PickIndex();
        byte[] response = await SendCommandAsync(idx, CommandBuilder.Build("GET", Encoding.UTF8.GetBytes(key)));

        try
        {
            var value = CloudProfileData.DeserializeFromBinary(new MemoryStream(response));
            return new CacheServiceResponse(CacheServiceResponseCode.Ok, value);
        }
        catch
        {
            return new CacheServiceResponse(ParseServerResponse(response));
        }
    }

    public async Task<CacheServiceResponse> SetAsync(string key, CloudProfileData value)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256);
        byte[] bytes;
        try
        {
            using var stream = new MemoryStream(buffer, 0, buffer.Length, writable: true);
            value.SerializeToBinary(stream);
            bytes = buffer.AsSpan(0, (int)stream.Position).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var idx = PickIndex();
        byte[] command = CommandBuilder.Build("SET", Encoding.UTF8.GetBytes(key), bytes);
        var response = await SendCommandAsync(idx, command);
        return new CacheServiceResponse(ParseServerResponse(response));
    }

    public async Task<CacheServiceResponse> DeleteAsync(string key)
    {
        var idx = PickIndex();
        byte[] command = CommandBuilder.Build("DELETE", Encoding.UTF8.GetBytes(key));
        var response = await SendCommandAsync(idx, command);
        return new CacheServiceResponse(ParseServerResponse(response));
    }

    public async Task<CacheStatistics?> GetStatisticsAsync()
    {
        var idx = PickIndex();
        byte[] command = CommandBuilder.Build("STATS", Array.Empty<byte>());
        byte[] response = await SendCommandAsync(idx, command);

        if (!CacheStatisticsSerializer.TryDeserialize(response, out var stats))
            return null;
        return stats;
    }

    public async Task ConnectAsync()
    {
        // Прогреваем все соединения параллельно, чтобы первые запросы не ждали handshakeов.
        var tasks = new Task[_clients.Length];
        for (int i = 0; i < _clients.Length; i++)
            tasks[i] = EnsureConnectedAsync(i);
        await Task.WhenAll(tasks);
    }

    private int PickIndex()
        => (int)((uint)Interlocked.Increment(ref _rrCounter) % (uint)_clients.Length);

    private async Task<byte[]> SendCommandAsync(int idx, byte[] data)
    {
        await EnsureConnectedAsync(idx);

        await _ioLocks[idx].WaitAsync();
        try
        {
            var stream = _clients[idx]!.GetStream();
            await stream.WriteAsync(data);

            // Читаем 4 байта длины (big-endian).
            byte[] lengthBuffer = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int n = await stream.ReadAsync(lengthBuffer.AsMemory(bytesRead, 4 - bytesRead));
                if (n == 0) throw new EndOfStreamException("Server closed connection while reading length");
                bytesRead += n;
            }
            int messageLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));

            if (messageLength > _bufferSize)
                throw new InvalidOperationException($"Received message length {messageLength} exceeds buffer size {_bufferSize}");

            byte[] response = new byte[messageLength];
            bytesRead = 0;
            while (bytesRead < messageLength)
            {
                int n = await stream.ReadAsync(response.AsMemory(bytesRead, messageLength - bytesRead));
                if (n == 0) throw new EndOfStreamException("Server closed connection while reading data");
                bytesRead += n;
            }

            return response;
        }
        finally
        {
            _ioLocks[idx].Release();
        }
    }

    private async Task EnsureConnectedAsync(int idx)
    {
        await _connectLocks[idx].WaitAsync();
        try
        {
            if (_clients[idx] != null && _clients[idx]!.Connected)
                return;

            _clients[idx]?.Close();
            _clients[idx]?.Dispose();

            var newClient = new TcpClient();
            await newClient.ConnectAsync(IPAddress.Parse(_host), _port);
            _clients[idx] = newClient;
        }
        finally
        {
            _connectLocks[idx].Release();
        }
    }

    public void Dispose()
    {
        if (_disposedValue) return;
        _disposedValue = true;

        for (int i = 0; i < _clients.Length; i++)
        {
            _clients[i]?.Close();
            _clients[i]?.Dispose();
            _connectLocks[i].Dispose();
            _ioLocks[i].Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private static CacheServiceResponseCode ParseServerResponse(byte[] response)
    {
        string responseString = Encoding.UTF8.GetString(response);
        return responseString switch
        {
            ServerResponses.AsString.OkResponse => CacheServiceResponseCode.Ok,
            ServerResponses.AsString.InvalidPayloadResponse => CacheServiceResponseCode.InvalidPayload,
            ServerResponses.AsString.UnknownCommandResponse => CacheServiceResponseCode.UnknownCommand,
            ServerResponses.AsString.NilResponse => CacheServiceResponseCode.Nil,
            _ => throw new InvalidOperationException($"Unexpected response from server: {responseString}")
        };
    }
}
