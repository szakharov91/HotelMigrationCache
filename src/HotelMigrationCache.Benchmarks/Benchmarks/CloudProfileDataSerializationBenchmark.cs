using System.Buffers;
using System.Globalization;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Benchmarks.Benchmarks;

public class CloudProfileDataSerializationBenchmark
{
    private CloudProfileData _profile = null!;

    [GlobalSetup]
    public void Setup() => _profile = new CloudProfileData
    {
        SrcId = "source-id-123",
        DstId = "destination-id-456",
        DateOfBirth = DateOnly.Parse("06-06-1990", CultureInfo.InvariantCulture),
        Firstname = "John",
        Lastname = "Weber",
        Email = "test@test.test",
        PhoneNumber = "+1234567890",
        MembershipLevel = "Gold",
        MembershipId = "membership-id-789",
        MembershipExpiredAt = DateTime.Parse("2028-06-06T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)
    };

    [Benchmark(Baseline = true)]
    public byte[] SystemTextJson() => JsonSerializer.SerializeToUtf8Bytes(_profile);

    [Benchmark]
    public byte[] GeneratedBinary()
    {
        using var stream = new MemoryStream();
        _profile.SerializeToBinary(stream);
        return stream.ToArray();
    }

    [Benchmark]
    public byte[] GeneratedBinaryPooled()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            using var stream = new MemoryStream(buffer, 0, buffer.Length, writable: true);
            _profile.SerializeToBinary(stream);
            return buffer.AsSpan(0, (int)stream.Position).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
