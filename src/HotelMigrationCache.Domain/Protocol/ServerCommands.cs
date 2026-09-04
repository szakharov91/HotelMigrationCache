namespace HotelMigrationCache.Shared.Protocol;

public enum ServerCommandKind
{
    Unknown,
    Get,
    Set,
    Delete
}

public readonly struct ServerCommands
{
    public static ReadOnlySpan<byte> Get => "GET"u8;
    public static ReadOnlySpan<byte> Set => "SET"u8;
    public static ReadOnlySpan<byte> Delete => "DELETE"u8;
}
