using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Core.Store;

public sealed class InMemoryKeyValueStore: IKeyValueStore
{
    #region private fields
    private readonly Dictionary<byte[], byte[]> _keyValuePairs;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _hitCount, _missCount, _setCount, _deleteCount;
    private volatile bool _disposedValue;
    #endregion

    #region .ctors
    public InMemoryKeyValueStore() => _keyValuePairs = new Dictionary<byte[], byte[]>(ByteArrayEqualityComparer.Instance);
    #endregion

    #region public methods
    public void Set(byte[] key, byte[] value)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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

    public CacheStatistics GetStatistics()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            return new CacheStatistics(
                _keyValuePairs.Count,
                Interlocked.Read(ref _hitCount),
                Interlocked.Read(ref _missCount),
                Interlocked.Read(ref _setCount),
                Interlocked.Read(ref _deleteCount));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion

    #region private methods
    private void Dispose(bool disposing)
    {
        if (_disposedValue)
            return;

        if (disposing)
        {
            try
            {
                _lock.EnterWriteLock();
                try
                {
                    if (_disposedValue)
                        return;

                    _keyValuePairs.Clear();
                    _disposedValue = true;
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }
            finally
            {
                _lock.Dispose();
            }
            
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposedValue)
            throw new ObjectDisposedException(nameof(InMemoryKeyValueStore));
    }

    #endregion

    private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayEqualityComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return x.AsSpan().SequenceEqual(y);
        }

        public int GetHashCode(byte[] obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
