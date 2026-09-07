namespace HotelMigrationCache.MigrationTool.Domain;

public sealed record SourceProfile(
    string SourceFileName,
    string RawXmlContent,
    string ProfileId,
    ProfileType Type,
    string? CorporateId,
    SourcePersonalInfo Personal,
    SourceContactInfo Contact,
    SourceIdentityDocument IdentityDocument,
    SourcePreferences? Preferences,
    SourceLoyaltyProgram? Loyalty,
    string? ArNumber,
    IReadOnlyList<SourceNegotiatedRate> NegotiatedRates,
    IReadOnlyList<SourceProfileRelationship> Relationships);

public sealed record SourcePersonalInfo(
    string FirstName,
    string LastName,
    string Gender,
    DateOnly BirthDate,
    string Nationality);

public sealed record SourceContactInfo(
    string Phone,
    string Email,
    SourceAddress Address);

public sealed record SourceAddress(
    string Street,
    string City,
    string PostalCode,
    string Country);

public sealed record SourceIdentityDocument(
    string DocType,
    string DocNumber,
    DateOnly IssueDate,
    DateOnly ExpiryDate,
    string IssuedBy);

public sealed record SourcePreferences(
    string? Language,
    string? Smoking,
    string? BedType);

public sealed record SourceLoyaltyProgram(
    LoyaltyLevel Level,
    string MemberId,
    DateOnly ExpiryDate);

public sealed record SourceNegotiatedRate(string RateCode, int DiscountPercent);

public sealed record SourceProfileRelationship(string RelatedProfileId, string RelationType);
