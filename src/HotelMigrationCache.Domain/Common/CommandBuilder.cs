using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace HotelMigrationCache.Shared.Common;

public static class CommandBuilder
{
    public static byte[] Build(string command, string key, object? value = null) 
        => value is not null ? Build(command, Encoding.UTF8.GetBytes(key), JsonSerializer.SerializeToUtf8Bytes(value)) : Build(command, Encoding.UTF8.GetBytes(key));

    public static byte[] Build(string command, byte[] key, byte[]? value = null)
    {
        byte[] cmd = Encoding.UTF8.GetBytes(command);
        // Вычисляем размер
        int totalLen = 1 + 1 + 1 + 1 + cmd.Length + 4 + key.Length + 4 + (value?.Length ?? 0) + 1 + 1;
        byte[] buffer = new byte[totalLen];
        int pos = 0;

        buffer[pos++] = 0x01; // SOH
        pos++; // место для контрольной суммы
        buffer[pos++] = 0x02; // STX

        buffer[pos++] = (byte)cmd.Length; // длина команды
        cmd.CopyTo(buffer, pos);
        pos += cmd.Length;

        // KeyLen и Key
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos, 4), key.Length);
        pos += 4;
        key.CopyTo(buffer, pos);
        pos += key.Length;

        // ValLen и Value
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(pos, 4), value?.Length ?? 0);
        pos += 4;
        value?.CopyTo(buffer, pos);
        pos += value?.Length ?? 0;

        buffer[pos++] = 0x03; // ETX
        buffer[pos++] = 0x04; // EOT

        // Вычисляем контрольную сумму (XOR от CmdNameLen до Value)
        byte sum = 0;
        for (int i = 3; i < pos - 2; i++) // от CmdNameLen до Value включительно
            sum ^= buffer[i];
        buffer[1] = sum;

        return buffer;
    }
}
