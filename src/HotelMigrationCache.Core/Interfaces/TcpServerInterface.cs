using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HotelMigrationCache.Core.Utils;
using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.Shared.Otel;
using HotelMigrationCache.Shared.Protocol;

namespace HotelMigrationCache.Core.Interfaces;

public sealed class TcpServerInterface(IKeyValueStore storage, int port, IPAddress ipAddress) : IProtocolInterface
{
    #region private fields
    private const int _receiveMessageByteCountRestriction = 4096;

    private readonly IKeyValueStore _storage = storage;
    private readonly int _port = port;
    private readonly IPAddress _ipAddress = ipAddress;
    private readonly CancellationTokenSource _cts = new CancellationTokenSource();
    private readonly int _bufferSize = 8 * 1024;
    private readonly SemaphoreSlim _semaphoreSlim = new SemaphoreSlim(4, 4);

    private Socket? _listener;
    private bool _disposedValue;
    #endregion

    #region public methods
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        var combinedToken = linkedCts.Token;

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(_ipAddress, _port));
        _listener.Listen();

        // В методе StartAsync после вызова Listen организуйте бесконечный асинхронный цикл (while(true))
        while (true)
        {
            if (combinedToken.IsCancellationRequested)
                break; // выходим по отмене из цикла безопасно

            await _semaphoreSlim.WaitAsync(cancellationToken);

            try
            {
                var clientSocket = await _listener.AcceptAsync(); // не используем using, т.к. обрабатываем клиента в ProcessClientAsync try-finally
                _ = ProcessClientAsync(clientSocket, combinedToken);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Error accepting client connection: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    #region private methods
    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing && _listener != null)
            {
                _cts.Cancel();
                _listener.Close(0); // немедленное закрытие
                _listener = null;
                _cts.Dispose();
            }

            _disposedValue = true;
        }
    }

    /// <summary> Ищем в хранилище по байтовому ключу</summary>
    private byte[] ProcessGetCommand(byte[] key)
        => _storage.TryGet(key, out byte[] value) ? value : ServerResponses.AsBytes.NilResponse;

    private byte[] ProcessSetCommand(byte[] key, byte[] value)
    {
        if(key == null || key.Length == 0)
            return ServerResponses.AsBytes.InvalidPayloadResponse;

        _storage.Set(key, value);
        return ServerResponses.AsBytes.OkResponse;
    }

    private byte[] ProcessDeleteCommand(byte[] key)
        => _storage.Delete(key) ? ServerResponses.AsBytes.OkResponse : ServerResponses.AsBytes.NilResponse;

    private async Task ProcessClientAsync(Socket clientSocket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientSocket);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested) // сразу завязываем цикл на токен
            {
                var memory = buffer.AsMemory();

                int bytesReceived = await clientSocket.ReceiveAsync(memory, SocketFlags.None, _cts.Token);
                if (bytesReceived == 0)
                    break;

                if (bytesReceived > _receiveMessageByteCountRestriction)
                    throw new SocketException((int)SocketError.OperationAborted, "Message size is too big!");

                using var activity = CommandTelemetry.ActivitySource.StartActivity(CommandTelemetry.Activities.CommandProcessing);

                ReadOnlyMemory<byte> readOnlyData = memory[..bytesReceived];
                var result = CommandParser.Parse(readOnlyData.Span);

                activity?.SetTag(CommandTelemetry.Tags.CommandName, result.Key.ToString());
                activity?.SetTag(CommandTelemetry.Tags.PayloadSize, bytesReceived);
                activity?.SetTag(CommandTelemetry.Tags.ClientEndpoint, clientSocket.RemoteEndPoint?.ToString());

                var stopwatch = Stopwatch.StartNew();

                byte[] response = ServerResponses.AsBytes.NilResponse;
                string key = result.Key.ToString();

                try
                {
                    ProcessMessage(readOnlyData.Span, clientSocket);
                }
                finally
                {
                    activity?.SetTag(CommandTelemetry.Tags.ResponseStatus, response);
                    stopwatch.Stop();
                    CommandTelemetry.CommandsProcessedCounter.Add(
                        1,
                        new KeyValuePair<string, object?>(CommandTelemetry.Tags.CommandName, key)
                        );
                    CommandTelemetry.CommandsDurationHistogram.Record(
                        stopwatch.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>(CommandTelemetry.Tags.CommandName, key)
                        );
                }

                await SendWithLengthAsync(clientSocket, response, cancellationToken);
            }
        }
        finally
        {
            clientSocket.Shutdown(SocketShutdown.Both);
            clientSocket.Close(); // Dispose можно не вызывать, так как он вызовется внутри Close()
            ArrayPool<byte>.Shared.Return(buffer, true);
            _semaphoreSlim.Release();
        }
    }

    private static async Task SendWithLengthAsync(Socket socket, byte[] data, CancellationToken ct)
    {
        // Длина в сетевом порядке (big-endian)
        int length = IPAddress.HostToNetworkOrder(data.Length);
        byte[] lengthBytes = BitConverter.GetBytes(length);
        await socket.SendAsync(lengthBytes, SocketFlags.None, ct);
        await socket.SendAsync(data, SocketFlags.None, ct);
    }

    private static void SendWithLength(Socket socket, byte[] data)
    {
        // Длина в сетевом порядке (big-endian)
        int length = IPAddress.HostToNetworkOrder(data.Length);
        byte[] lengthBytes = BitConverter.GetBytes(length);
        socket.Send(lengthBytes, SocketFlags.None);
        socket.Send(data, SocketFlags.None);
    }

    private void ProcessMessage(ReadOnlySpan<byte> message, Socket clientSocket)
    {
        var parsed = CommandParser.Parse(message);
        if (!parsed.IsValid)
        {
            SendWithLength(clientSocket, ServerResponses.AsBytes.InvalidPayloadResponse);
            return;
        }

        byte[] response;
        response = parsed.GetCommandKind() switch
        {
            ServerCommandKind.Get => ProcessGetCommand(parsed.Key.ToArray()),
            ServerCommandKind.Set => ProcessSetCommand(parsed.Key.ToArray(), parsed.Value.ToArray()),
            ServerCommandKind.Delete => ProcessDeleteCommand(parsed.Key.ToArray()),
            _ => ServerResponses.AsBytes.UnknownCommandResponse
        };

        SendWithLength(clientSocket, response);
    }
    #endregion
}
