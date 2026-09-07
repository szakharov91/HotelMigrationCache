namespace HotelMigrationCache.Shared;

/// <summary>Общие лимиты для клиента и сервера кэша.</summary>
public static class ServerLimits
{
    /// <summary>
    /// Максимум одновременных TCP-подключений, которые сервер кэша принимает
    /// (через SemaphoreSlim в TcpServerInterface). Клиентский pool не должен превышать эту величину.
    /// </summary>
    public const int MaxServerConnections = 256;
}
