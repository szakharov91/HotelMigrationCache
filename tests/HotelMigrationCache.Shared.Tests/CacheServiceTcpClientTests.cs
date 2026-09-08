using FluentAssertions;
using HotelMigrationCache.Shared.Common;
using HotelMigrationCache.Shared.Tests.Fixtures;
using HotelMigrationCache.Shared.Utils;

namespace HotelMigrationCache.Shared.Tests;

/// <summary>
/// Интеграционные тесты клиент↔сервер. Каждый тест: свежий <see cref="Core.Interfaces.TcpServerInterface"/>
/// на loopback + отдельный <see cref="CacheServiceTcpClient"/> с явно управляемым lifetime.
/// </summary>
public sealed class CacheServiceTcpClientTests : TcpServerTestBase
{
    private const string _host = "127.0.0.1";

    [Fact]
    public async Task SetAsync_thenGetAsync_returnsStoredValue()
    {
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 1);
        await client.ConnectAsync();
        var profile = ProfileFactory.Sample();

        var setResponse = await client.SetAsync(profile.SrcId!, profile);
        var getResponse = await client.GetAsync<CloudProfileData>(profile.SrcId!);

        setResponse.ResponseCode.Should().Be(CacheServiceResponseCode.Ok);
        getResponse.ResponseCode.Should().Be(CacheServiceResponseCode.Ok);
        getResponse.Value.Should().NotBeNull();
        getResponse.Value!.SrcId.Should().Be(profile.SrcId);
        getResponse.Value.DstId.Should().Be(profile.DstId);
        getResponse.Value.Firstname.Should().Be(profile.Firstname);
        getResponse.Value.Lastname.Should().Be(profile.Lastname);
        getResponse.Value.DateOfBirth.Should().Be(profile.DateOfBirth);
        getResponse.Value.MembershipLevel.Should().Be(profile.MembershipLevel);
        getResponse.Value.MembershipExpiredAt.Should().Be(profile.MembershipExpiredAt);
    }

    [Fact]
    public async Task GetAsync_missingKey_returnsNil()
    {
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 1);
        await client.ConnectAsync();

        var response = await client.GetAsync<CloudProfileData>("missing-key");

        response.ResponseCode.Should().Be(CacheServiceResponseCode.Nil);
        response.Value.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_existingKey_removesEntry()
    {
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 1);
        await client.ConnectAsync();
        var profile = ProfileFactory.Sample("P-001");
        await client.SetAsync(profile.SrcId!, profile);

        var deleteResponse = await client.DeleteAsync(profile.SrcId!);
        var getResponse = await client.GetAsync<CloudProfileData>(profile.SrcId!);

        deleteResponse.ResponseCode.Should().Be(CacheServiceResponseCode.Ok);
        getResponse.ResponseCode.Should().Be(CacheServiceResponseCode.Nil);
    }

    [Fact]
    public async Task DeleteAsync_missingKey_returnsNil()
    {
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 1);
        await client.ConnectAsync();

        var response = await client.DeleteAsync("never-existed");

        response.ResponseCode.Should().Be(CacheServiceResponseCode.Nil);
    }

    [Fact]
    public async Task SetAsync_existingKey_overwritesValue()
    {
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 1);
        await client.ConnectAsync();

        var v1 = ProfileFactory.Sample("P-777");
        v1.Firstname = "Original";
        var v2 = ProfileFactory.Sample("P-777");
        v2.Firstname = "Updated";

        await client.SetAsync(v1.SrcId!, v1);
        await client.SetAsync(v2.SrcId!, v2);
        var response = await client.GetAsync<CloudProfileData>(v1.SrcId!);

        response.ResponseCode.Should().Be(CacheServiceResponseCode.Ok);
        response.Value!.Firstname.Should().Be("Updated");
    }

    [Fact]
    public async Task GetStatisticsAsync_reflectsExecutedOperations()
    {
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 1);
        await client.ConnectAsync();

        var profile = ProfileFactory.Sample("P-stats");
        await client.SetAsync(profile.SrcId!, profile);
        await client.GetAsync<CloudProfileData>(profile.SrcId!);          // hit
        await client.GetAsync<CloudProfileData>("nope");                  // miss
        await client.DeleteAsync(profile.SrcId!);

        var stats = await client.GetStatisticsAsync();

        stats.Should().NotBeNull();
        stats!.Value.SetCount.Should().Be(1);
        stats.Value.HitCount.Should().Be(1);
        stats.Value.MissCount.Should().Be(1);
        stats.Value.DeleteCount.Should().Be(1);
    }

    [Fact]
    public async Task ParallelSetsThroughPool_allPersistedCorrectly()
    {
        // Пул из 10 сокетов — round-robin PickIndex + per-slot io lock.
        // Каждая пара SET+GET использует один и тот же ключ, идентичность значения проверяется на выходе.
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 10);
        await client.ConnectAsync();

        const int count = 200;
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            var p = ProfileFactory.Sample($"P-{i:D4}");
            p.Firstname = $"Guest-{i}";
            await client.SetAsync(p.SrcId!, p);
            var got = await client.GetAsync<CloudProfileData>(p.SrcId!);
            return (Expected: p, got.ResponseCode, got.Value);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().OnlyContain(r => r.ResponseCode == CacheServiceResponseCode.Ok);
        results.Should().OnlyContain(r => r.Value != null);
        foreach (var r in results)
            r.Value!.Firstname.Should().Be(r.Expected.Firstname);

        var stats = await client.GetStatisticsAsync();
        stats.Should().NotBeNull();
        stats!.Value.SetCount.Should().Be(count);
        stats.Value.HitCount.Should().Be(count);
    }

    [Fact]
    public async Task PoolReusesConnections_acrossManySequentialCalls()
    {
        // Проверяем, что per-slot semaphore корректно освобождает соединение и очередь ответов
        // не смешивается (не поедет framing) при интенсивной последовательной работе через пул.
        using var client = new CacheServiceTcpClient(_host, Port, poolSize: 4);
        await client.ConnectAsync();

        var profile = ProfileFactory.Sample("P-reuse");
        await client.SetAsync(profile.SrcId!, profile);

        // 4 сокета × 25 итераций = гарантированно каждый слот отработает несколько раз.
        for (int i = 0; i < 100; i++)
        {
            var response = await client.GetAsync<CloudProfileData>(profile.SrcId!);
            response.ResponseCode.Should().Be(CacheServiceResponseCode.Ok);
            response.Value!.SrcId.Should().Be("P-reuse");
        }
    }
}
