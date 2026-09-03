using FluentAssertions;
using HotelMigrationCache.Core.Store;
using HotelMigrationCache.Shared.Common;

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
}
