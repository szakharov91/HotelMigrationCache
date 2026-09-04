using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Protocol;

namespace HotelMigrationCache.Shared.Utils;

public sealed class CacheServiceTcpClient : ICacheServiceClient
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _bufferSize = 1024 * 1024;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private TcpClient? _client;
    private bool _disposedValue;

    public CacheServiceTcpClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task<CloudProfileData> GetAsync(string key)
    {
        byte[] response = await SendCommand(CommandBuilder.Build("GET", key, null));
        return CloudProfileData.DeserializeFromBinary(new MemoryStream(response));
    }

    public async Task<CacheServiceResponseCode> SetAsync(string key, CloudProfileData value)
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

        byte[] command = CommandBuilder.Build("SET", key, bytes);

        var response = await SendCommand(command);
        return ParseServerResponse(response);
    }

    public async Task<CacheServiceResponseCode> DeleteAsync(string key)
    {
        byte[] command = CommandBuilder.Build("DELETE", key, Array.Empty<byte>());

        var response = await SendCommand(command);

        return ParseServerResponse(response);
    }

    private async Task<byte[]> SendCommand(byte[] data)
    {
        await EnsureConnectedAsync();

        var stream = _client!.GetStream();
        await stream.WriteAsync(data);

        // Читаем 4 байта длины (big-endian)
        byte[] lengthBuffer = new byte[4];
        int bytesRead = 0;
        while (bytesRead < 4)
        {
            int n = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
            if (n == 0) throw new EndOfStreamException("Server closed connection while reading length");
            bytesRead += n;
        }
        int messageLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));

        if(messageLength > _bufferSize)
        {
            throw new InvalidOperationException($"Received message length {messageLength} exceeds buffer size {_bufferSize}");
        }

        // Читаем ровно messageLength байт
        byte[] response = new byte[messageLength];
        bytesRead = 0;
        while (bytesRead < messageLength)
        {
            int n = await stream.ReadAsync(response, bytesRead, messageLength - bytesRead);
            if (n == 0) throw new EndOfStreamException("Server closed connection while reading data");
            bytesRead += n;
        }

        return response;
    }

    private async Task EnsureConnectedAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            if (_client != null && _client.Connected)
                return;

            // Закрываем старый, если есть
            _client?.Close();
            _client?.Dispose();

            // Создаём новый и подключаем
            _client = new TcpClient();
            await _client.ConnectAsync(IPAddress.Parse(_host), _port);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _client?.Close();
                _client?.Dispose();
            }

            _disposedValue = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
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
