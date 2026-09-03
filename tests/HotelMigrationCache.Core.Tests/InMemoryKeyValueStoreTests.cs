using System.Text;
using FluentAssertions;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.Shared.Common;
using Newtonsoft.Json.Linq;

namespace HotelMigrationCache.Core.Tests;

public class InMemoryKeyValueStoreTests
{
    private IKeyValueStore<CloudProfileData>? _store;

    [Fact]
    public void SetAndGet_ReturnsValue()
    {
        // Arrange
        _store = new InMemoryKeyValueStore<CloudProfileData>();
        var key = "guest123";
        var value = new CloudProfileData();

        // Act
        _store.Set(key, value);

        // Assert
        _store.TryGet(key, out var result).Should().BeTrue();
        result.Should().BeEquivalentTo(value);
    }

    [Fact]
    public void Get_NonExistingKey_ReturnsFalse()
    {
        // Arrange
        _store = new InMemoryKeyValueStore<CloudProfileData>();
        var key = "missing";

        // Act & Assert
        _store.TryGet(key, out _).Should().BeFalse();
    }

    [Fact]
    public void Delete_RemovesValue()
    {
        // Arrange
        _store = new InMemoryKeyValueStore<CloudProfileData>();
        var key = "guest123";
        var value = new CloudProfileData();
        _store.Set(key, value);

        // Act
        _store.Delete(key);

        // Assert
        _store.TryGet(key, out var result).Should().BeFalse();
        result.Should().BeEquivalentTo(default(CloudProfileData));
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        _store = new InMemoryKeyValueStore<CloudProfileData>();
        var key1 = "guest123";
        var key2 = "guest456";
        var key3 = "guest789";

        // Act
        _store.Set(key1, new CloudProfileData { SrcId = "guest123", DstId = "cloud123"});
        _store.Set(key2, new CloudProfileData { SrcId = "guest456", DstId = "cloud456"});
        _store.Set(key3, new CloudProfileData { SrcId = "guest789", DstId = "cloud789" });

        _store.TryGet("nonexistent", out _);
        _store.TryGet("guest456", out _);
        bool isNotDeleted = _store.Delete("nonexistent");
        bool isDeleted = _store.Delete("guest123");

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
        _store = new InMemoryKeyValueStore<CloudProfileData>();
        int threads = 10;
        int operationsPerThread = 1000;
        var tasks = new List<Task>();

        for (int t = 0; t < threads; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < operationsPerThread; i++)
                {
                    var key = $"key-{i % 100}";
                    _store.Set(key, new CloudProfileData());
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
        _store = new InMemoryKeyValueStore<CloudProfileData>();
        var key = "guest123";
        var value = new CloudProfileData();

        // Act
        _store.Set(key, value);
        _store.Dispose();

        // Assert
        var action = () => _store.TryGet("key", out _);
        action.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimesWithoutError()
    {
        var store = new InMemoryKeyValueStore<CloudProfileData>();

        Action act = () =>
        {
            store.Dispose();
            store.Dispose();
        };

        act.Should().NotThrow();
    }
}
