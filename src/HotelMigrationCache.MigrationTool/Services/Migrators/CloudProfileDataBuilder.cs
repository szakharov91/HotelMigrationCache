using HotelMigrationCache.MigrationTool.Domain;
using HotelMigrationCache.Shared.Common;

namespace HotelMigrationCache.MigrationTool.Services.Migrators;

// Собирает CloudProfileData из разобранного XML + свежий DstId.
// Вынесено, потому что тем же способом строит запись оба мигратора: профайл-мигратор
// прогревает кэш после успеха, а резервейшн-мигратор — при промахе кэша, читая XML из локальной БД.
internal static class CloudProfileDataBuilder
{
    public static CloudProfileData Build(string srcId, string dstId, SourceProfile source)
    {
        var loyalty = source.Loyalty;
        return new CloudProfileData
        {
            SrcId = srcId,
            DstId = dstId,
            Firstname = source.Personal.FirstName,
            Lastname = source.Personal.LastName,
            Email = source.Contact.Email,
            PhoneNumber = source.Contact.Phone,
            MembershipLevel = loyalty is null ? null : loyalty.Level.ToString(),
            MembershipId = loyalty?.MemberId,
            MembershipExpiredAt = loyalty is null
                ? default
                : loyalty.ExpiryDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            // DateOfBirth в CloudProfileData get-only и не сериализуется — заполнять нечем.
        };
    }
}
