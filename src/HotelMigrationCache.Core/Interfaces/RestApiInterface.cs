namespace HotelMigrationCache.Core.Interfaces;

public class RestApiInterface: IProtocolInterface
{
    private bool _disposedValue;

    public Task RunAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // Dispose managed state (managed objects)
            }

            _disposedValue = true;
        }
    }
}
