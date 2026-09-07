namespace HotelMigrationCache.Shared.Protocol;

public static class ServerResponses
{
    public static class AsString
    {
        public const string OkResponse = "OK\r\n";
        public const string NilResponse = "(nil)\r\n";
        public const string UnknownCommandResponse = "-ERR Unknown command\r\n";
        public const string InvalidPayloadResponse = "-ERR Invalid payload\r\n";
    }

    public static class AsBytes
    {
        public static byte[] OkResponse => "OK\r\n"u8.ToArray();

        public static byte[] NilResponse => "(nil)\r\n"u8.ToArray();

        public static byte[] UnknownCommandResponse => "-ERR Unknown command\r\n"u8.ToArray();

        public static byte[] InvalidPayloadResponse => "-ERR Invalid payload\r\n"u8.ToArray();
    }
}
