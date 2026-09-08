using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Protocol;
using HotelMigrationCache.Shared.Tests.Fixtures;

namespace HotelMigrationCache.Shared.Tests;

/// <summary>
/// Прямые тесты wire-протокола <c>TcpServerInterface</c> — обходим клиент, шлём байты через голый TCP.
/// Покрывает то, что клиент не достаёт: неизвестная команда, невалидный payload, framing ответа.
/// </summary>
public sealed class TcpServerInterfaceProtocolTests : TcpServerTestBase
{
    [Fact]
    public async Task UnknownCommand_returnsUnknownCommandResponse()
    {
        byte[] command = CommandBuilder.Build("UNKNOWN_CMD", Encoding.UTF8.GetBytes("key"));

        byte[] response = await SendRawAsync(command);

        response.Should().BeEquivalentTo(ServerResponses.AsBytes.UnknownCommandResponse);
    }

    [Fact]
    public async Task MalformedPayload_returnsInvalidPayloadResponse()
    {
        // Мусор без SOH/STX/ETX/EOT — парсер должен вернуть IsValid=false.
        byte[] junk = new byte[] { 0xFF, 0xEE, 0xDD, 0xCC, 0xBB };

        byte[] response = await SendRawAsync(junk);

        response.Should().BeEquivalentTo(ServerResponses.AsBytes.InvalidPayloadResponse);
    }

    [Fact]
    public async Task SetCommand_withEmptyKey_returnsInvalidPayloadResponse()
    {
        byte[] command = CommandBuilder.Build("SET", Array.Empty<byte>(), Encoding.UTF8.GetBytes("value"));

        byte[] response = await SendRawAsync(command);

        response.Should().BeEquivalentTo(ServerResponses.AsBytes.InvalidPayloadResponse);
    }

    [Fact]
    public async Task ResponseFraming_lengthPrefixIsBigEndianAndMatchesPayload()
    {
        byte[] command = CommandBuilder.Build("GET", Encoding.UTF8.GetBytes("nonexistent"));

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(command);

        // Читаем сначала 4 байта big-endian длины.
        byte[] lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer);
        int declaredLength = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));

        byte[] payload = new byte[declaredLength];
        await ReadExactlyAsync(stream, payload);

        declaredLength.Should().Be(ServerResponses.AsBytes.NilResponse.Length);
        payload.Should().BeEquivalentTo(ServerResponses.AsBytes.NilResponse);
    }

    private async Task<byte[]> SendRawAsync(byte[] command)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(command);

        byte[] lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer);
        int length = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(lengthBuffer, 0));

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload);
        return payload;
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (n == 0) throw new EndOfStreamException($"Read only {read}/{buffer.Length} bytes before EOF");
            read += n;
        }
    }
}
