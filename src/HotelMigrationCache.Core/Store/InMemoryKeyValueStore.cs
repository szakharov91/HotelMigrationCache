using System.Buffers;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Core.Store;

public class InMemoryKeyValueStore<TValue>: IKeyValueStore<TValue>
    where TValue : IBinarySerializable<TValue>
{
    #region private fields
    private readonly Dictionary<string, byte[]> _keyValuePairs;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private long _hitCount, _missCount, _setCount, _deleteCount;
    private bool _disposedValue;
    #endregion

    #region .ctors
    public InMemoryKeyValueStore() => _keyValuePairs = new Dictionary<string, byte[]>();
    #endregion

    #region public methods
    public void Set(string key, TValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(256);
        byte[] bytes;
        try
        {
            using var stream = new MemoryStream(buffer, 0, buffer.Length, writable: true);
            value.SerializeToBinary(stream);
            bytes = buffer.AsSpan(0, (int)stream.Position).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _lock.EnterWriteLock();
        try
        {
            _keyValuePairs[key] = bytes;
            Interlocked.Increment(ref _setCount);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool TryGet(string key, out TValue value)
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
            using var readStream = new MemoryStream(bytes);
            value = TValue.DeserializeFromBinary(readStream);
            return true;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public bool Delete(string key)
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

    #region private and protected methods
    protected virtual void Dispose(bool disposing)
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
