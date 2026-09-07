using System.Buffers.Binary;

namespace HotelMigrationCache.Shared.Common;

// Плоский бинарный формат STATS-ответа: 5 × Int64 little-endian = 40 байт.
// Простой и быстрый — не тянем BinaryWriter/сгенерированный сериализатор.
public static class CacheStatisticsSerializer
{
    public const int SerializedSize = 5 * sizeof(long);

    public static byte[] Serialize(CacheStatistics stats)
    {
        var buffer = new byte[SerializedSize];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span[..8], stats.Count);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8, 8), stats.HitCount);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(16, 8), stats.MissCount);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(24, 8), stats.SetCount);
        BinaryPrimitives.WriteInt64LittleEndian(span.Slice(32, 8), stats.DeleteCount);
        return buffer;
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> data, out CacheStatistics stats)
    {
        if (data.Length != SerializedSize)
        {
            stats = default;
            return false;
        }

        stats = new CacheStatistics(
            Count:       BinaryPrimitives.ReadInt64LittleEndian(data[..8]),
            HitCount:    BinaryPrimitives.ReadInt64LittleEndian(data.Slice(8, 8)),
            MissCount:   BinaryPrimitives.ReadInt64LittleEndian(data.Slice(16, 8)),
            SetCount:    BinaryPrimitives.ReadInt64LittleEndian(data.Slice(24, 8)),
            DeleteCount: BinaryPrimitives.ReadInt64LittleEndian(data.Slice(32, 8)));
        return true;
    }
}
