using System.Buffers.Binary;
using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Core.Utils;

public static class CommandParser
{
    public static CacheParsedCommand Parse(ReadOnlySpan<byte> buffer)
    {
        // Минимальная длина: SOH + ControlSum + STX + CmdNameLen(1) + CmdName(хотя бы 1) + KeyLen(4) + ValLen(4) + ETX + EOT = 13
        if (buffer.Length < 13)
            return default;

        // Проверяем маркеры
        if (buffer[0] != AsciiChars.SOH || buffer[2] != AsciiChars.STX || buffer[^2] != AsciiChars.ETX || buffer[^1] != AsciiChars.EOT)
            return default;

        // Контрольная сумма
        byte controlSum = buffer[1];

        // Извлекаем длину имени команды (после STX)
        int cmdNameLen = buffer[3];
        if (cmdNameLen == 0 || 4 + cmdNameLen > buffer.Length - 2)
            return default;

        // Имя команды
        ReadOnlySpan<byte> commandName = buffer.Slice(4, cmdNameLen);

        // Смещение для KeyLen
        int keyLenPos = 4 + cmdNameLen;
        // KeyLen (4 байта little-endian)
        int keyLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(keyLenPos, 4));
        if (keyLen < 0 || keyLenPos + 4 + keyLen > buffer.Length - 2)
            return default;

        // Ключ
        ReadOnlySpan<byte> key = buffer.Slice(keyLenPos + 4, keyLen);

        // ValLen
        int valLenPos = keyLenPos + 4 + keyLen;
        int valLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(valLenPos, 4));
        if (valLen < 0 || valLenPos + 4 + valLen != buffer.Length - 2) // должно заканчиваться перед ETX
            return default;

        // Значение
        ReadOnlySpan<byte> value = buffer.Slice(valLenPos + 4, valLen);

        // Проверяем контрольную сумму (XOR всех байтов от CmdNameLen до Value включительно)
        byte computed = 0;
        foreach (byte b in buffer.Slice(3, buffer.Length - 5)) // от CmdNameLen до Value
            computed ^= b;

        if (computed != controlSum)
            return default;

        return new CacheParsedCommand
        {
            CommandName = commandName,
            Key = key,
            Value = value,
            IsValid = true
        };
    }
}