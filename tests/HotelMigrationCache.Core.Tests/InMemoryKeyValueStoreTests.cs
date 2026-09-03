using System;
using System.Collections.Generic;
using System.Text;

namespace HotelMigrationCache.Core.Tests;

public class InMemoryKeyValueStoreTests
{
    [Fact]
    public void SetAndGet_ReturnsValue()
    {
        // Arrange
        var store = new InMemoryKeyValueStore();
        var key = "testKey";
        var value = "testValue";
        // Act
        store.Set(key, value);
        var result = store.Get(key);
        // Assert
        result.Should().Be(value);
    }
}
