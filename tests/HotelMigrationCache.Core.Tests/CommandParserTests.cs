using System.Buffers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using HotelMigrationCache.Core.Utils;
using HotelMigrationCache.Shared.Common;
using Newtonsoft.Json.Linq;

namespace HotelMigrationCache.Core.Tests;

public class CommandParserTests
{
    [Fact]
    public void Parse_ValidCommand_ReturnsParsed()
    {
        var cloudProfile = new CloudProfileData
        {
            SrcId = "SRC-123",
            DstId = "DST-456",
            Firstname = "John",
            Lastname = "Doe",
            Email = "john.doe@example.com",
            PhoneNumber = "+1234567890",
            MembershipLevel = "Gold",
            MembershipId = "MEM-987654",
            MembershipExpiredAt = new DateTime(2027, 12, 31, 23, 59, 59, DateTimeKind.Utc)
        };

        byte[] data = CommandBuilder.Build("SET", "guest1", cloudProfile);
        var parsed = CommandParser.Parse(data);

        parsed.IsValid.Should().BeTrue();
        Encoding.UTF8.GetString(parsed.CommandName).Should().Be("SET");
        Encoding.UTF8.GetString(parsed.Key).Should().Be("guest1");
        parsed.Value.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(cloudProfile));
    }

    [Fact]
    public void Parse_BadControlSum_ReturnsInvalid()
    {
        byte[] data = CommandBuilder.Build("SET", "guest1", "profile-1");
        data[1] ^= 0xFF; // портим контрольную сумму
        var parsed = CommandParser.Parse(data);
        parsed.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Parse_MissingEOT_ReturnsInvalid()
    {
        byte[] data = CommandBuilder.Build("SET", "guest1", "profile-1");
        data[^1] = 0x00; // убираем EOT
        var parsed = CommandParser.Parse(data);
        parsed.IsValid.Should().BeFalse();
    }
}
