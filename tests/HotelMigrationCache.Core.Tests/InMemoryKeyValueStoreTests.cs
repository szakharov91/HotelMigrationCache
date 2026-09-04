using System.Text;
using FluentAssertions;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Contracts;

namespace HotelMigrationCache.Core.Tests;

public class InMemoryKeyValueStoreTests
{
    private IKeyValueStore? _store;

    [Fact]
    public void SetAndGet_ReturnsValue()
    {
        // Arrange
        _store = new InMemoryKeyValueStore();
        var key = Encoding.UTF8.GetBytes("guest123");
        var value = new CloudProfileData();
        var buffer = new byte[4096];
        value.SerializeToBinary(new MemoryStream(buffer));

        // Act
        _store.Set(key, buffer);

        // Assert
        _store.TryGet(key, out var result).Should().BeTrue();
        result.Should().BeEquivalentTo(buffer);
    }

    [Fact]
    public void Get_NonExistingKey_ReturnsFalse()
    {
        // Arrange
        _store = new InMemoryKeyValueStore();
        var key = Encoding.UTF8.GetBytes("missing");

        // Act & Assert
        _store.TryGet(key, out _).Should().BeFalse();
    }

    [Fact]
    public void Delete_RemovesValue()
    {
        // Arrange
        _store = new InMemoryKeyValueStore();
        var key = Encoding.UTF8.GetBytes("guest123");
        var value = new CloudProfileData();
        var buffer = new byte[4096];
        value.SerializeToBinary(new MemoryStream(buffer));
        _store.Set(key, buffer);

        // Act
        _store.Delete(key);

        // Assert
        _store.TryGet(key, out var result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        _store = new InMemoryKeyValueStore();
        var key1 = Encoding.UTF8.GetBytes("guest123");
        var key2 = Encoding.UTF8.GetBytes("guest456");
        var key3 = Encoding.UTF8.GetBytes("guest789");
        var val1 = new CloudProfileData { SrcId = "guest123", DstId = "cloud123" };
        var val2 = new CloudProfileData { SrcId = "guest456", DstId = "cloud456" };
        var val3 = new CloudProfileData { SrcId = "guest789", DstId = "cloud789" };

        var buffer1 = new byte[4096];
        val1.SerializeToBinary(new MemoryStream(buffer1));
        var buffer2 = new byte[4096];
        val2.SerializeToBinary(new MemoryStream(buffer2));
        var buffer3 = new byte[4096];
        val3.SerializeToBinary(new MemoryStream(buffer3));

        // Act
        _store.Set(key1, buffer1);
        _store.Set(key2, buffer2);
        _store.Set(key3, buffer3);

        _store.TryGet(Encoding.UTF8.GetBytes("nonexistent"), out _);
        _store.TryGet(Encoding.UTF8.GetBytes("guest456"), out _);
        bool isNotDeleted = _store.Delete(Encoding.UTF8.GetBytes("nonexistent"));
        bool isDeleted = _store.Delete(Encoding.UTF8.GetBytes("guest123"));

        // Assert
        isNotDeleted.Should().BeFalse();
        isDeleted.Should().BeTrue();

        var stats = _store.GetStatistics();
        stats.SetCount.Should().Be(3);
        stats.MissCount.Should().Be(1);
        stats.DeleteCount.Should().Be(1);
        stats.HitCount.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentAccess_IsThreadSafe()
    {
        _store = new InMemoryKeyValueStore();
        int threads = 10;
        int operationsPerThread = 1000;
        var tasks = new List<Task>();

        for (int t = 0; t < threads; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    var key = Encoding.UTF8.GetBytes($"key-{i % 100}");
                    var val = new CloudProfileData();
                    var buffer = new byte[4096];
                    val.SerializeToBinary(new MemoryStream(buffer));
                    _store.Set(key, buffer);
                    _store.TryGet(key, out _);
                }
            }));
        }

        await Task.WhenAll(tasks);

        var stats = _store.GetStatistics();
        stats.SetCount.Should().Be(threads * operationsPerThread);
        stats.Count.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public void Dispose_ReleasesResources()
    {
        // Arrange
        _store = new InMemoryKeyValueStore();
        var key = Encoding.UTF8.GetBytes("guest123");
        var value = new CloudProfileData();
        var buffer = new byte[4096];
        value.SerializeToBinary(new MemoryStream(buffer));

        // Act
        _store.Set(key, buffer);
        _store.Dispose();

        // Assert
        var action = () => _store.TryGet(Encoding.UTF8.GetBytes("key"), out _);
        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesWithoutError()
    {
        var store = new InMemoryKeyValueStore();

        Action act = () =>
        {
            store.Dispose();
            store.Dispose();
        };

        act.Should().NotThrow();
    }
}
