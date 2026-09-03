namespace HotelMigrationCache.Shared.Contracts;

/// <summary>
/// Represents a type that can be serialized to and deserialized from a binary format.
/// </summary>
public interface IBinarySerializable<TSelf>
    where TSelf : IBinarySerializable<TSelf>
{
    void SerializeToBinary(Stream stream);

    static abstract TSelf DeserializeFromBinary(Stream stream);
}
