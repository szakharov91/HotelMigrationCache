using FluentAssertions;
using HotelMigrationCache.Shared;
using HotelMigrationCache.Shared.Utils;

namespace HotelMigrationCache.Shared.Tests;

/// <summary>
/// Проверки диапазона <c>poolSize</c> — валидация констрейнта против <see cref="ServerLimits.MaxServerConnections"/>.
/// Сервер не поднимаем: конструктор бросает до попытки соединения.
/// </summary>
public sealed class CacheServiceTcpClientPoolValidationTests
{
    private const string _host = "127.0.0.1";
    private const int _dummyPort = 9999; // не используется — падаем до open

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(ServerLimits.MaxServerConnections + 1)]
    [InlineData(1000)]
    public void Constructor_poolSizeOutOfRange_throwsArgumentOutOfRange(int poolSize)
    {
        Action act = () => _ = new CacheServiceTcpClient(_host, _dummyPort, poolSize);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("poolSize");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(ServerLimits.MaxServerConnections)]
    public void Constructor_poolSizeInRange_doesNotThrow(int poolSize)
    {
        // ConnectAsync() не вызываем — конструктор не должен трогать сеть.
        Action act = () =>
        {
            using var _ = new CacheServiceTcpClient(_host, _dummyPort, poolSize);
        };

        act.Should().NotThrow();
    }
}
