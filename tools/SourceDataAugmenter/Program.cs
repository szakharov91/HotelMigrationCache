using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SourceDataAugmenter;

// One-shot утилита. Обогащает исходные XML профайлов блоком <LoyaltyProgram>
// и XML броней блоком <AccompanyingGuests>. Детерминирована по seed.
// Идемпотентна: файлы, где нужный блок уже есть, пропускаются.
public static class Program
{
    private static readonly UTF8Encoding _utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private const double ProfileLoyaltyRatio = 0.30;
    private const double BookingAccompanyingRatio = 0.20;
    private const int MinAccompanying = 1;
    private const int MaxAccompanying = 3;

    public static int Main(string[] args)
    {
        var parsed = ParseArgs(args);
        if (parsed is null)
        {
            PrintUsage();
            return 1;
        }

        var (profilesDir, bookingsDir, seed, dryRun) = parsed.Value;

        if (!Directory.Exists(profilesDir))
        {
            Console.Error.WriteLine($"Profiles dir not found: {profilesDir}");
            return 2;
        }
        if (!Directory.Exists(bookingsDir))
        {
            Console.Error.WriteLine($"Bookings dir not found: {bookingsDir}");
            return 2;
        }

        Console.WriteLine($"Profiles dir : {profilesDir}");
        Console.WriteLine($"Bookings dir : {bookingsDir}");
        Console.WriteLine($"Seed         : {seed}");
        Console.WriteLine($"Dry-run      : {(dryRun ? "yes (no files will be written)" : "no")}");
        Console.WriteLine();

        var profileIds = Directory.EnumerateFiles(profilesDir, "*.xml")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Console.WriteLine($"Profiles found: {profileIds.Length:N0}");
        var profileStats = AugmentProfiles(profilesDir, seed, dryRun);
        Console.WriteLine($"  augmented    : {profileStats.augmented:N0}");
        Console.WriteLine($"  skipped (has): {profileStats.alreadyHad:N0}");
        Console.WriteLine($"  untouched    : {profileStats.total - profileStats.augmented - profileStats.alreadyHad:N0}");

        var bookingFiles = Directory.EnumerateFiles(bookingsDir, "*.xml").ToArray();
        Console.WriteLine($"Bookings found: {bookingFiles.Length:N0}");
        var bookingStats = AugmentBookings(bookingsDir, profileIds, seed, dryRun);
        Console.WriteLine($"  augmented    : {bookingStats.augmented:N0}");
        Console.WriteLine($"  skipped (has): {bookingStats.alreadyHad:N0}");
        Console.WriteLine($"  untouched    : {bookingStats.total - bookingStats.augmented - bookingStats.alreadyHad:N0}");

        return 0;
    }

    private static (int total, int augmented, int alreadyHad) AugmentProfiles(string dir, int seed, bool dryRun)
    {
        int total = 0, augmented = 0, alreadyHad = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            total++;
            var xml = File.ReadAllText(file);

            if (xml.Contains("<LoyaltyProgram>", StringComparison.Ordinal))
            {
                alreadyHad++;
                continue;
            }

            // Seed на файл — идемпотентно и не зависит от порядка/пропусков в других файлах.
            var name = Path.GetFileNameWithoutExtension(file);
            var rng = new Random(StableSeed(seed, name, "profile"));

            var roll = rng.NextDouble();
            if (roll >= ProfileLoyaltyRatio)
                continue;

            var level = PickLoyaltyLevel(rng);
            var memberId = $"LP-{rng.Next(100_000_000, 1_000_000_000).ToString(CultureInfo.InvariantCulture)}";
            var expiryYears = rng.Next(1, 4);
            var expiryDate = DateTime.UtcNow.Date.AddYears(expiryYears).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var block = $"<LoyaltyProgram><Level>{level}</Level><MemberID>{memberId}</MemberID><ExpiryDate>{expiryDate}</ExpiryDate></LoyaltyProgram>";
            var updated = xml.Replace("</Profile>", block + "</Profile>", StringComparison.Ordinal);
            if (ReferenceEquals(updated, xml))
                continue;

            if (!dryRun)
                File.WriteAllText(file, updated, _utf8Bom);
            augmented++;
        }

        return (total, augmented, alreadyHad);
    }

    private static (int total, int augmented, int alreadyHad) AugmentBookings(string dir, string[] profileIds, int seed, bool dryRun)
    {
        int total = 0, augmented = 0, alreadyHad = 0;
        var profileIdRegex = new Regex("<ProfileID>([^<]+)</ProfileID>", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(dir, "*.xml").OrderBy(f => f, StringComparer.Ordinal))
        {
            total++;
            var xml = File.ReadAllText(file);

            if (xml.Contains("<AccompanyingGuests>", StringComparison.Ordinal))
            {
                alreadyHad++;
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file);
            var rng = new Random(StableSeed(seed, name, "booking"));

            var roll = rng.NextDouble();
            if (roll >= BookingAccompanyingRatio)
                continue;

            var guestCount = rng.Next(MinAccompanying, MaxAccompanying + 1);

            var mainMatch = profileIdRegex.Match(xml);
            if (!mainMatch.Success)
                continue;
            var mainId = mainMatch.Groups[1].Value;

            var picks = new HashSet<string>(StringComparer.Ordinal);
            int guard = 0;
            while (picks.Count < guestCount && guard < guestCount * 10)
            {
                var candidate = profileIds[rng.Next(profileIds.Length)];
                if (!string.Equals(candidate, mainId, StringComparison.Ordinal))
                    picks.Add(candidate);
                guard++;
            }
            if (picks.Count == 0)
                continue;

            var inner = string.Concat(picks.Select(p => $"<GuestProfileID>{p}</GuestProfileID>"));
            var block = $"<AccompanyingGuests>{inner}</AccompanyingGuests>";
            var updated = xml.Replace("</Booking>", block + "</Booking>", StringComparison.Ordinal);
            if (ReferenceEquals(updated, xml))
                continue;

            if (!dryRun)
                File.WriteAllText(file, updated, _utf8Bom);
            augmented++;
        }

        return (total, augmented, alreadyHad);
    }

    // Стабильный хеш через FNV-1a 32-bit. Не зависит от процесса/машины (в отличие от HashCode/string.GetHashCode).
    private static int StableSeed(int seed, string name, string tag)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = MixIn(hash, (uint)seed);
            foreach (var c in name)
                hash = (hash ^ c) * 16777619u;
            foreach (var c in tag)
                hash = (hash ^ c) * 16777619u;
            return (int)hash;
        }
    }

    private static uint MixIn(uint hash, uint value)
    {
        unchecked
        {
            hash ^= (byte)value;         hash *= 16777619u;
            hash ^= (byte)(value >> 8);  hash *= 16777619u;
            hash ^= (byte)(value >> 16); hash *= 16777619u;
            hash ^= (byte)(value >> 24); hash *= 16777619u;
            return hash;
        }
    }

    private static string PickLoyaltyLevel(Random rng) => rng.NextDouble() switch
    {
        < 0.40 => "Basic",
        < 0.70 => "Silver",
        < 0.90 => "Gold",
        < 0.98 => "Platinum",
        _ => "Diamond",
    };

    private static (string profilesDir, string bookingsDir, int seed, bool dryRun)? ParseArgs(string[] args)
    {
        string? profiles = null, bookings = null;
        int seed = 42;
        bool dryRun = false;

        int i = 0;
        while (i < args.Length)
        {
            var arg = args[i];
            var hasNext = i + 1 < args.Length;

            if (arg == "--profiles" && hasNext) { profiles = args[i + 1]; i += 2; continue; }
            if (arg == "--bookings" && hasNext) { bookings = args[i + 1]; i += 2; continue; }
            if (arg == "--seed" && hasNext)
            {
                if (!int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                    return null;
                i += 2; continue;
            }
            if (arg == "--dry-run") { dryRun = true; i++; continue; }

            return null;
        }

        if (profiles is null || bookings is null)
            return null;

        return (profiles, bookings, seed, dryRun);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SourceDataAugmenter --profiles <dir> --bookings <dir> [--seed 42] [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("Idempotent: files already containing <LoyaltyProgram> / <AccompanyingGuests> are skipped.");
    }
}
