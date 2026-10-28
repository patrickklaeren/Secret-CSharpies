using System.Security.Claims;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SecretSanta.Data;

namespace SecretSanta.Services;

public sealed record ShipmentView(ShipmentStatus Status, DateOnly? EstimatedArrival, DateTime? UpdatedUtc);

public sealed record Dashboard(Participant Person, SantaCycle Cycle, ShipmentView? Outgoing, ShipmentView? Incoming);

public sealed record AdminDashboard(SantaCycle Cycle, List<Participant> People, List<GiftMatch> Matches);

public sealed class SantaService(DatabaseVault vault, IConfiguration config, TimeProvider clock, IMemoryCache cache, ILogger<SantaService> logger)
{
    private const string ParticipantAvatarCacheKey = "ParticipantAvatarUrls";
    private static readonly HttpClient WebhookClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<string?> RandomParticipantAvatarAsync()
    {
        if (!vault.IsUnlocked)
            return null;

        var avatars = await cache.GetOrCreateAsync(ParticipantAvatarCacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(4);
            return vault.UseAsync(db => db.Participants
                .Where(p => p.SubmittedUtc != null && !p.Removed && p.AvatarUrl != null && p.AvatarUrl != "")
                .Select(p => p.AvatarUrl!).ToListAsync());
        });
        return avatars is not { Count: > 0 } ? null : avatars[Random.Shared.Next(avatars.Count)];
    }

    public bool IsAdmin(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true &&
        !string.IsNullOrWhiteSpace(config["AdminDiscordId"]) &&
        string.Equals(user.FindFirstValue(ClaimTypes.NameIdentifier), config["AdminDiscordId"], StringComparison.Ordinal);

    private void RequireAdmin(ClaimsPrincipal user)
    {
        if (!IsAdmin(user))
            throw new UnauthorizedAccessException("Admin access is required.");
    }

    private static string Id(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
        ? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException("Sign in with Discord.")
        : throw new UnauthorizedAccessException("Sign in with Discord.");

    public Task UnlockAsync(ClaimsPrincipal user, string password, bool initialize)
    {
        RequireAdmin(user);

        return vault.UnlockAsync(password, initialize);
    }

    public Task<Dashboard> DashboardAsync(ClaimsPrincipal user) => vault.UseAsync(async db =>
    {
        var id = Id(user);
        var person = await db.Participants.FindAsync(id);

        if (person is null)
        {
            person = new Participant { DiscordId = id, FirstSeenUtc = clock.GetUtcNow().UtcDateTime };
            db.Participants.Add(person);
        }

        person.Username = user.Identity!.Name!;
        person.AvatarUrl = user.FindFirstValue("avatar");
        await db.SaveChangesAsync();
        var cycle = await db.Cycles.SingleAsync();
        var distributed = SantaCycle.HasPassed(cycle.DistributionUtc, clock.GetUtcNow().UtcDateTime) && !person.Removed;
        ShipmentView? View(GiftMatch? match) => match is null ? null : new ShipmentView(match.Status, match.EstimatedArrival, match.UpdatedUtc);

        return new Dashboard(person, cycle,
            distributed ? View(await db.Matches.SingleOrDefaultAsync(m => m.SenderId == id)) : null,
            distributed ? View(await db.Matches.SingleOrDefaultAsync(m => m.RecipientId == id)) : null);
    });

    public async Task<bool> SaveParticipationAsync(ClaimsPrincipal user, Participant form)
    {
        var firstSignup = await vault.UseAsync(async db =>
        {
        var id = Id(user);
        var cycle = await db.Cycles.SingleAsync();

        if (cycle.Locked(clock.GetUtcNow().UtcDateTime))
            throw new InvalidOperationException("Sign-ups are locked. Your form cannot be changed.");

        var person = await db.Participants.FindAsync(id) ?? throw new InvalidOperationException("Reload your profile first.");

        if (person.Removed)
            throw new InvalidOperationException("An organizer removed your participation. Contact them to rejoin.");

        form.Validate();
        person.Country = form.Country;
        person.Send = form.Send;
        person.Receive = form.Receive;
        person.GiftPreference = form.GiftPreference;
        person.ShippingPreference = form.ShippingPreference;
        person.Name = form.Name.Trim();
        person.Phone = form.Phone.Trim();
        person.Line1 = form.Line1.Trim();
        person.Line2 = form.Line2.Trim();
        person.Line3 = form.Line3.Trim();
        person.Line4 = form.Line4.Trim();
        person.ContactNotes = form.ContactNotes.Trim();
        person.Bio = form.Bio.Trim();
        person.Consent = form.Consent;
        var firstSignup = person.SubmittedUtc is null;
        person.SubmittedUtc ??= clock.GetUtcNow().UtcDateTime;
        person.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        cache.Remove(ParticipantAvatarCacheKey);

        return firstSignup;
        });

        if (firstSignup && config["Discord:SignupWebhookUrl"] is { Length: > 0 } webhook)
        {
            if (Uri.TryCreate(webhook, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            {
                try
                {
                    using var response = await WebhookClient.PostAsJsonAsync(uri, new
                    {
                        content = $"{user.Identity!.Name} has signed up to be a secret Santa!",
                        allowed_mentions = new { parse = Array.Empty<string>() }
                    });
                    if (!response.IsSuccessStatusCode)
                        logger.LogWarning("Discord signup notification failed with status {StatusCode}", (int)response.StatusCode);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                    logger.LogWarning("Discord signup notification could not be sent.");
                }
            }
            else
                logger.LogWarning("Discord signup webhook URL must be an absolute HTTPS URL.");
        }

        return true;
    }

    public Task<bool> SaveShipmentAsync(ClaimsPrincipal user, ShipmentStatus status, DateOnly? eta) => vault.UseAsync(async db =>
    {
        var id = Id(user);
        var person = await db.Participants.FindAsync(id);

        if (person is null || person.Removed)
            throw new InvalidOperationException("You are not an active participant.");

        var cycle = await db.Cycles.SingleAsync();

        if (!SantaCycle.HasPassed(cycle.DistributionUtc, clock.GetUtcNow().UtcDateTime))
            throw new InvalidOperationException("Shipment updates open on the distribution date.");

        if (!Enum.IsDefined(status))
            throw new InvalidOperationException("Choose a valid shipment status.");

        var match = await db.Matches.SingleOrDefaultAsync(m => m.SenderId == id) ?? throw new InvalidOperationException("You have no saved recipient yet.");
        match.Status = status;
        match.EstimatedArrival = eta;
        match.UpdatedUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();

        return true;
    });

    public Task<AdminDashboard> AdminAsync(ClaimsPrincipal user)
    {
        RequireAdmin(user);

        return vault.UseAsync(async db => new AdminDashboard(await db.Cycles.SingleAsync(), await db.Participants.OrderBy(p => p.Username).ToListAsync(), await db.Matches.ToListAsync()));
    }

    public Task<bool> ConfigureAsync(ClaimsPrincipal user, SantaCycle form)
    {
        RequireAdmin(user);

        return vault.UseAsync(async db =>
        {
            static DateTime? Midnight(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
            var dates = new[] { form.LockInUtc, form.DistributionUtc, form.ShipByUtc }.Where(d => d is not null).Select(d => d!.Value.Date).ToArray();

            if (!dates.SequenceEqual(dates.Order()))
                throw new InvalidOperationException("Dates must be in lock-in, distribution, ship-by order.");

            if (form.Greeting.Length > 4000)
                throw new InvalidOperationException("The greeting must be 4,000 characters or fewer.");

            var cycle = await db.Cycles.SingleAsync();
            cycle.LockInUtc = Midnight(form.LockInUtc);
            cycle.DistributionUtc = Midnight(form.DistributionUtc);
            cycle.ShipByUtc = Midnight(form.ShipByUtc);
            cycle.Greeting = form.Greeting.Trim();
            await db.SaveChangesAsync();

            return true;
        });
    }

    public Task<bool> SetRemovedAsync(ClaimsPrincipal user, string id, bool removed)
    {
        RequireAdmin(user);

        return vault.UseAsync(async db =>
        {
            var person = await db.Participants.FindAsync(id) ?? throw new InvalidOperationException("Participant not found.");

            if (person.Removed == removed)
                return true;

            await using var transaction = await db.Database.BeginTransactionAsync();
            person.Removed = removed;
            await db.Matches.ExecuteDeleteAsync();
            (await db.Cycles.SingleAsync()).DrawnUtc = null;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return true;
        });
    }

    public Task<DrawResult> DrawAsync(ClaimsPrincipal user)
    {
        RequireAdmin(user);

        return vault.UseAsync(async db =>
        {
            var cycle = await db.Cycles.SingleAsync();

            if (!SantaCycle.HasPassed(cycle.LockInUtc, clock.GetUtcNow().UtcDateTime))
                throw new InvalidOperationException("Set a lock-in date and wait for it to pass before drawing.");

            var people = await db.Participants.Where(p => !p.Removed && p.SubmittedUtc != null).ToListAsync();
            var result = Matcher.Draw(people);

            if (!result.Complete)
                return result;

            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.Matches.ExecuteDeleteAsync();
            db.Matches.AddRange(result.Pairs.Select(p => new GiftMatch { SenderId = p.SenderId, RecipientId = p.RecipientId }));
            cycle.DrawnUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return result;
        });
    }

    public Task<string> CopyDetailsAsync(ClaimsPrincipal user, string id)
    {
        RequireAdmin(user);

        return vault.UseAsync(async db =>
        {
            var p = await db.Participants.FindAsync(id) ?? throw new InvalidOperationException("Participant not found.");
            var cycle = await db.Cycles.SingleAsync();

            return string.Join("\n",
                new[]
                {
                    cycle.Greeting, "", $"@{p.Username}", Countries.Name(p.Country), $"Gift preference: {Labels.Gift(p.GiftPreference)}", "", p.Name, p.Phone, p.Line1, p.Line2, p.Line3, p.Line4,
                    p.ContactNotes, "", "Notes to Santa:", p.Bio
                }.Where(s => s is not null));
        });
    }
}

public static class Labels
{
    public static string Gift(GiftPreference p) => p switch
    {
        GiftPreference.Anything => "Receive anything", GiftPreference.Physical => "Physical gifts only", GiftPreference.Digital => "Digital / virtual gifts only", _ => "N/A (give-only)"
    };

    public static string Shipping(ShippingPreference p) => p switch
    {
        ShippingPreference.Anywhere => "Ship anywhere, including internationally", ShippingPreference.RegionalDelivery => "Order from regional Amazon / local delivery",
        ShippingPreference.DigitalOnly => "Buy a digital gift", _ => "Prefer to ship only domestically"
    };
}
