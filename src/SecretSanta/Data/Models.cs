using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace SecretSanta.Data;

public enum GiftPreference
{
    Anything,
    Physical,
    Digital,
    GiveOnly
}

public enum ShippingPreference
{
    Anywhere,
    RegionalDelivery,
    DigitalOnly,
    PreferDomestic
}

public enum ShipmentStatus
{
    Preparing,
    Shipped,
    Delivered
}

public sealed class Participant
{
    [Key] public string DiscordId { get; set; } = "";
    [MaxLength(80)] public string Username { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public bool Removed { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public DateTime? SubmittedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
    public string Country { get; set; } = "";
    public bool Send { get; set; } = true;
    public bool Receive { get; set; } = true;
    public GiftPreference GiftPreference { get; set; }
    public ShippingPreference ShippingPreference { get; set; }
    public string Name { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string Line3 { get; set; } = "";
    public string Line4 { get; set; } = "";
    public string ContactNotes { get; set; } = "";
    public string Bio { get; set; } = "";
    public bool Consent { get; set; }

    public Participant Copy() => (Participant)MemberwiseClone();

    public void Validate() => Validate(3);

    public void Validate(int throughStep)
    {
        if (!Countries.All.ContainsKey(Country))
            throw new InvalidOperationException("Choose your region.");

        if (!Send && !Receive)
            throw new InvalidOperationException("Choose send gifts, receive gifts, or both.");

        if (!Enum.IsDefined(GiftPreference) || !Enum.IsDefined(ShippingPreference))
            throw new InvalidOperationException("Choose valid gift and shipping preferences.");

        if (Receive && GiftPreference == GiftPreference.GiveOnly)
            throw new InvalidOperationException("Choose what you would like to receive.");

        if (!Receive)
            GiftPreference = GiftPreference.GiveOnly;

        if (throughStep == 1)
            return;

        if (Receive && GiftPreference != GiftPreference.Digital && (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Line1)))
            throw new InvalidOperationException("Physical gifts need a postal name and address (at least line 1).");

        if (Receive && GiftPreference == GiftPreference.Digital && string.IsNullOrWhiteSpace(ContactNotes))
            throw new InvalidOperationException("Add a digital delivery address, username, or wishlist in other notes.");

        foreach (var value in new[] { Name, Phone, Line1, Line2, Line3, Line4 })
            if (value.Length > 300)
                throw new InvalidOperationException("Address fields must be 300 characters or fewer.");

        if (ContactNotes.Length > 4000)
            throw new InvalidOperationException("Notes must be 4,000 characters or fewer.");

        if (throughStep == 2)
            return;

        if (Receive && string.IsNullOrWhiteSpace(Bio))
            throw new InvalidOperationException("Give your Santa a few ideas in your notes.");

        if (Bio.Length > 4000)
            throw new InvalidOperationException("Notes must be 4,000 characters or fewer.");
    }
}

public sealed class SantaCycle
{
    public int Id { get; set; } = 1;
    public DateTime? LockInUtc { get; set; }
    public DateTime? DistributionUtc { get; set; }
    public DateTime? ShipByUtc { get; set; }
    public string Greeting { get; set; } = "Hello Santa! Here are the details for your gift exchange:";
    public DateTime? DrawnUtc { get; set; }
    public bool Locked(DateTime now) => HasPassed(LockInUtc, now) || DrawnUtc is not null;
    public static bool HasPassed(DateTime? date, DateTime now) => date is not null && date <= now;

    public string Phase(DateTime now) => HasPassed(ShipByUtc, now) ? "Gifts on their way" :
        HasPassed(DistributionUtc, now) ? "Time to send some joy" :
        DrawnUtc is not null ? "Santas are assigned" :
        HasPassed(LockInUtc, now) ? "The list is locked" : "Sign-ups are open";
}

public sealed class GiftMatch
{
    public int Id { get; set; }
    public string SenderId { get; set; } = "";
    public string RecipientId { get; set; } = "";
    public ShipmentStatus Status { get; set; }
    public DateOnly? EstimatedArrival { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

public static class Countries
{
    public static readonly IReadOnlyDictionary<string, string> Regions = new Dictionary<string, string>
    {
        ["North America"] = "North America",
        ["South America"] = "South America",
        ["Europe"] = "Europe",
        ["Asia"] = "Asia",
        ["Australia"] = "Australia",
        ["Other"] = "Other 🐎"
    };

    public static readonly IReadOnlyDictionary<string, string> All = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Select(c => new RegionInfo(c.Name)).DistinctBy(r => r.TwoLetterISORegionName)
        .Select(r => new KeyValuePair<string, string>(r.TwoLetterISORegionName, r.EnglishName))
        .Concat(Regions).ToDictionary(r => r.Key, r => r.Value);

    public static string Name(string code) => All.GetValueOrDefault(code, code);
}
