using Bogus;
using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.Benchmarks.Utils;

public static class CloudProfileDataGenerator
{
    // Единый экземпляр Faker для переиспользования (опционально, но эффективно)
    private static readonly Faker<CloudProfileData> _faker = new Faker<CloudProfileData>()
        .RuleFor(x => x.SrcId, f => f.Random.AlphaNumeric(10))                           // "aB3dEfGhIj"
        .RuleFor(x => x.DstId, f => f.Random.AlphaNumeric(10))                           // "KlMnOpQrSt"
        .RuleFor(x => x.DateOfBirth, f => DateOnly.FromDateTime(
            f.Date.Past(30, DateTime.Now.AddYears(-20))                                // возраст 20–50 лет
        ))
        .RuleFor(x => x.Firstname, f => f.Name.FirstName())                              // "John"
        .RuleFor(x => x.Lastname, f => f.Name.LastName())                               // "Weber"
        .RuleFor(x => x.Email, f => f.Internet.Email())                                 // "test@test.test"
        .RuleFor(x => x.PhoneNumber, f => f.Phone.PhoneNumber("+###########"))          // "+1234567890"
        .RuleFor(x => x.MembershipLevel, f => f.Random.ArrayElement(new[]               // "Gold", "Silver", "Platinum", "Diamond"
        {
            "Gold", "Silver", "Platinum", "Diamond", "Basic", "Premium"
        }))
        .RuleFor(x => x.MembershipId, f => f.Random.AlphaNumeric(12))                   // "membership-id-789"
        .RuleFor(x => x.MembershipExpiredAt, f => f.Date.Future(2, DateTime.UtcNow)     // дата в будущем на 2 года
            .ToUniversalTime());                                                         // приводим к UTC

    /// <summary>
    /// Генерирует один случайный объект CloudProfileData.
    /// </summary>
    public static CloudProfileData Generate() => _faker.Generate();

    /// <summary>
    /// Генерирует указанное количество объектов.
    /// </summary>
    public static List<CloudProfileData> GenerateMany(int count) => _faker.Generate(count);
}
