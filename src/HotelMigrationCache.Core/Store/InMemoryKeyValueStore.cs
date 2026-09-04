using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Core.Store;

public sealed class InMemoryKeyValueStore: IKeyValueStore
{
    #region private fields
    private readonly Dictionary<byte[], byte[]> _keyValuePairs;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _hitCount, _missCount, _setCount, _deleteCount;
    private bool _disposedValue;
    #endregion

    #region .ctors
    public InMemoryKeyValueStore() => _keyValuePairs = new Dictionary<byte[], byte[]>();
    #endregion

    #region public methods
    public void Set(byte[] key, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        _lock.EnterWriteLock();
        try
        {
            _keyValuePairs[key] = value;
            Interlocked.Increment(ref _setCount);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool TryGet(byte[] key, out byte[] value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterReadLock();
        try
        {
            if (!_keyValuePairs.TryGetValue(key, out byte[]? bytes))
            {
                Interlocked.Increment(ref _missCount);
                value = default!;
                return false;
            }

            Interlocked.Increment(ref _hitCount);
            value = bytes;
            return true;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool Delete(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterWriteLock();
        try
        {
            if (_keyValuePairs.Remove(key))
            {
                Interlocked.Increment(ref _deleteCount);
                return true;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        return false;
    }

    public CacheStatistics GetStatistics() => new(_keyValuePairs.Count, _hitCount, _missCount, _setCount, _deleteCount);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    #region private methods
    private void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _lock.Dispose();
            }

            _disposedValue = true;
        }
    }

    #endregion
}
