using HotelMigrationCache.Shared.Contracts;
using HotelMigrationCache.SourceGen.Attributes;

namespace HotelMigrationCache.Shared.Common;

[@GenerateBinarySerializerAttribute]
public partial class CloudProfileData: IBinarySerializable<CloudProfileData>
{
    public string? SrcId { get; set; }
    public string? DstId { get; set; }
    public DateOnly DateOfBirth { get; set; }
    public string? Firstname { get; set; }
    public string? Lastname { get; set; }
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? MembershipLevel { get; set; }
    public string? MembershipId { get; set; }
    public DateTime MembershipExpiredAt { get; set; }
}
