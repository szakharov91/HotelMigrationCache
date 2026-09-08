using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Shared.Tests.Fixtures;

/// <summary>Фабрика тестовых DTO — заранее заполненный <see cref="CloudProfileData"/> с покрытием
/// всех поддерживаемых source-gen типов: string?, DateOnly, DateTime, nullable-строки.</summary>
internal static class ProfileFactory
{
    public static CloudProfileData Sample(string srcId = "P-000042") => new()
    {
        SrcId = srcId,
        DstId = "cloud-dst-" + srcId,
        Firstname = "John",
        Lastname = "Doe",
        Email = "john.doe@example.com",
        PhoneNumber = "+1234567890",
        DateOfBirth = new DateOnly(1990, 5, 15),
        MembershipLevel = "Gold",
        MembershipId = "M-42",
        MembershipExpiredAt = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };
}
