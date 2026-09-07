namespace HotelMigrationCache.Core.Interfaces;

public interface IProtocolInterface: IDisposable
{
    Task RunAsync(CancellationToken cancellationToken = default);
}
