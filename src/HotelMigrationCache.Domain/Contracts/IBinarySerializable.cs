using System.Buffers;

namespace HotelMigrationCache.Shared.Contracts;

public interface IBinarySerializable<TSelf>
    where TSelf : IBinarySerializable<TSelf>
{
    void SerializeToBinary(Stream stream);

    static abstract TSelf DeserializeFromBinary(Stream stream);
}
