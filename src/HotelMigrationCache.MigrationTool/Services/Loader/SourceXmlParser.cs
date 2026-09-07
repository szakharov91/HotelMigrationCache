using System.Globalization;
using System.Xml.Linq;
using HotelMigrationCache.MigrationTool.Domain;

namespace HotelMigrationCache.MigrationTool.Services.Loader;

// Чистые pure-функции разбора XML → DTO. Не работают с диском, чтобы миграторы
// могли парсить FileRawContent из БД без обращения к исходным файлам.
public static class SourceXmlParser
{
    private static readonly XName _xsiNil = XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance");

    public static SourceProfile ParseProfile(string rawXml, string sourceFilename)
    {
        var doc = XDocument.Parse(rawXml);
        var root = doc.Root ?? throw new InvalidOperationException($"Empty XML: {sourceFilename}");

        var profileId = RequiredText(root, "ProfileID");
        var profileType = ParseProfileType(OptionalText(root, "ProfileType"));
        var corporateId = OptionalText(root, "CorporateId");
        var personal = ParsePersonalInfo(RequiredElement(root, "PersonalInfo"));
        var contact = ParseContactInfo(RequiredElement(root, "ContactInfo"));
        var identity = ParseIdentityDocument(RequiredElement(root, "IdentityDocument"));
        var preferences = ParsePreferences(root.Element("Preferences"));
        var loyalty = ParseLoyaltyProgram(root.Element("LoyaltyProgram"));
        var arNumber = OptionalText(root, "ARNumber");
        var negotiatedRates = ParseNegotiatedRates(root.Element("NegotiatedRates"));
        var relationships = ParseRelationships(root.Element("Relationships"));

        return new SourceProfile(sourceFilename, rawXml, profileId, profileType, corporateId,
            personal, contact, identity, preferences, loyalty,
            arNumber, negotiatedRates, relationships);
    }

    private static ProfileType ParseProfileType(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ProfileType.Guest;
        return Enum.TryParse<ProfileType>(text, ignoreCase: true, out var t) ? t : ProfileType.Guest;
    }

    private static IReadOnlyList<SourceNegotiatedRate> ParseNegotiatedRates(XElement? el)
    {
        if (el is null) return Array.Empty<SourceNegotiatedRate>();
        return el.Elements("Rate")
            .Select(r => new SourceNegotiatedRate(
                RateCode: RequiredText(r, "RateCode"),
                DiscountPercent: int.Parse(RequiredText(r, "DiscountPercent"), CultureInfo.InvariantCulture)))
            .ToList();
    }

    private static IReadOnlyList<SourceProfileRelationship> ParseRelationships(XElement? el)
    {
        if (el is null) return Array.Empty<SourceProfileRelationship>();
        return el.Elements("Relationship")
            .Select(r => new SourceProfileRelationship(
                RelatedProfileId: RequiredText(r, "RelatedProfileID"),
                RelationType: RequiredText(r, "RelationType")))
            .ToList();
    }

    public static SourceReservation ParseReservation(string rawXml, string sourceFilename)
    {
        var doc = XDocument.Parse(rawXml);
        var root = doc.Root ?? throw new InvalidOperationException($"Empty XML: {sourceFilename}");

        var bookingId = RequiredText(root, "BookingID");
        var mainProfileId = RequiredText(root, "ProfileID");

        var stay = RequiredElement(root, "StayPeriod");
        var checkIn = ParseDateOnly(RequiredText(stay, "CheckIn"));
        var checkOut = ParseDateOnly(RequiredText(stay, "CheckOut"));

        var room = ParseRoomDetails(RequiredElement(root, "RoomDetails"));
        var guests = ParseGuestComposition(RequiredElement(root, "GuestComposition"));
        var status = RequiredText(root, "Status");
        var totalPrice = ParseMoney(RequiredElement(root, "TotalPrice"))
            ?? throw new InvalidOperationException($"TotalPrice is required: {sourceFilename}");
        var depositPaid = ParseMoney(root.Element("DepositPaid"));

        var services = root.Element("Services")?.Elements("Service").Select(ParseService).ToList()
                       ?? new List<SourceReservationService>();

        var remarks = OptionalText(root, "Remarks");

        var accompanying = root.Element("AccompanyingGuests")?
            .Elements("GuestProfileID")
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList()
            ?? new List<string>();

        var attachedCompanies = root.Element("AttachedCompanies")?
            .Elements("CompanyProfileID")
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList()
            ?? new List<string>();

        var billingCompany = OptionalText(root, "BillingCompanyProfileID");

        return new SourceReservation(
            sourceFilename, rawXml, bookingId, mainProfileId,
            checkIn, checkOut, room, guests, status, totalPrice, depositPaid,
            services, remarks, accompanying, attachedCompanies, billingCompany);
    }

    private static SourcePersonalInfo ParsePersonalInfo(XElement el) => new(
        FirstName: RequiredText(el, "FirstName"),
        LastName: RequiredText(el, "LastName"),
        Gender: RequiredText(el, "Gender"),
        BirthDate: ParseDateOnly(RequiredText(el, "BirthDate")),
        Nationality: RequiredText(el, "Nationality"));

    private static SourceContactInfo ParseContactInfo(XElement el) => new(
        Phone: RequiredText(el, "Phone"),
        Email: RequiredText(el, "Email"),
        Address: ParseAddress(RequiredElement(el, "Address")));

    private static SourceAddress ParseAddress(XElement el) => new(
        Street: RequiredText(el, "Street"),
        City: RequiredText(el, "City"),
        PostalCode: RequiredText(el, "PostalCode"),
        Country: RequiredText(el, "Country"));

    private static SourceIdentityDocument ParseIdentityDocument(XElement el) => new(
        DocType: RequiredText(el, "DocType"),
        DocNumber: RequiredText(el, "DocNumber"),
        IssueDate: ParseDateOnly(RequiredText(el, "IssueDate")),
        ExpiryDate: ParseDateOnly(RequiredText(el, "ExpiryDate")),
        IssuedBy: RequiredText(el, "IssuedBy"));

    private static SourcePreferences? ParsePreferences(XElement? el) => el is null
        ? null
        : new SourcePreferences(
            Language: OptionalText(el, "Language"),
            Smoking: OptionalText(el, "Smoking"),
            BedType: OptionalText(el, "BedType"));

    private static SourceLoyaltyProgram? ParseLoyaltyProgram(XElement? el)
    {
        if (el is null) return null;

        var levelText = RequiredText(el, "Level");
        if (!Enum.TryParse<LoyaltyLevel>(levelText, ignoreCase: true, out var level))
            level = LoyaltyLevel.Basic; // на случай нераспознанных значений — не падаем, откатываемся на Basic

        return new SourceLoyaltyProgram(
            Level: level,
            MemberId: RequiredText(el, "MemberID"),
            ExpiryDate: ParseDateOnly(RequiredText(el, "ExpiryDate")));
    }

    private static SourceRoomDetails ParseRoomDetails(XElement el) => new(
        RoomType: RequiredText(el, "RoomType"),
        RoomNumber: OptionalText(el, "RoomNumber"),
        BedConfiguration: OptionalText(el, "BedConfiguration"));

    private static SourceGuestComposition ParseGuestComposition(XElement el)
    {
        var adults = int.Parse(RequiredText(el, "AdultCount"), CultureInfo.InvariantCulture);
        var children = int.Parse(RequiredText(el, "ChildCount"), CultureInfo.InvariantCulture);
        var ages = el.Element("ChildrenAges")?
            .Elements("Age")
            .Where(a => !IsNil(a) && !string.IsNullOrWhiteSpace(a.Value))
            .Select(a => int.Parse(a.Value, CultureInfo.InvariantCulture))
            .ToList()
            ?? new List<int>();
        return new SourceGuestComposition(adults, children, ages);
    }

    private static SourceReservationService ParseService(XElement el)
    {
        var serviceType = RequiredText(el, "Type");

        DateOnly? serviceDate = null;
        var dateEl = el.Element("Date");
        if (dateEl is not null && !IsNil(dateEl) && !string.IsNullOrWhiteSpace(dateEl.Value))
            serviceDate = ParseDateOnly(dateEl.Value);

        var isDaily = ParseNullableBool(el.Element("Daily"));
        var price = ParseMoney(el.Element("Price"));
        var dailyRate = ParseMoney(el.Element("DailyRate") ?? el.Element("PricePerDay"));

        return new SourceReservationService(serviceType, serviceDate, isDaily, price, dailyRate);
    }

    private static SourceMoney? ParseMoney(XElement? el)
    {
        if (el is null || IsNil(el) || string.IsNullOrWhiteSpace(el.Value))
            return null;

        var value = decimal.Parse(el.Value, NumberStyles.Number, CultureInfo.InvariantCulture);
        var currency = (string?)el.Attribute("currency") ?? "USD";
        return new SourceMoney(value, currency);
    }

    private static bool? ParseNullableBool(XElement? el)
    {
        if (el is null || IsNil(el) || string.IsNullOrWhiteSpace(el.Value))
            return null;
        return bool.Parse(el.Value);
    }

    private static DateOnly ParseDateOnly(string text)
        => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static bool IsNil(XElement el)
        => (string?)el.Attribute(_xsiNil) == "true";

    private static XElement RequiredElement(XElement parent, string name)
        => parent.Element(name) ?? throw new InvalidOperationException($"Missing element <{name}> under <{parent.Name.LocalName}>");

    private static string RequiredText(XElement parent, string childName)
    {
        var child = RequiredElement(parent, childName);
        var text = child.Value.Trim();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException($"Element <{childName}> is empty");
        return text;
    }

    private static string? OptionalText(XElement parent, string childName)
    {
        var child = parent.Element(childName);
        if (child is null || IsNil(child))
            return null;
        var text = child.Value.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
