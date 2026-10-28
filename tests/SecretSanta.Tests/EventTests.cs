using System.Security.Claims;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SecretSanta.Data;
using SecretSanta.Services;
using Xunit;

namespace SecretSanta.Tests;

public sealed class EventTests : IDisposable
{
    private const string Password = "a-strong-test-password-42";
    private readonly ClaimsPrincipal admin = User("admin-id", "organizer");
    private readonly TestClock clock = new();
    private readonly IConfiguration configuration;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "secret-santa-tests-" + Guid.NewGuid());
    private readonly Host environment;
    private readonly SantaService service;
    private readonly DatabaseVault vault;

    public EventTests()
    {
        configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory, ["AdminDiscordId"] = "admin-id" }).Build();
        environment = new Host(directory);
        vault = new DatabaseVault(configuration, environment);
        service = new SantaService(vault, configuration, clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    private static ClaimsPrincipal User(string id, string username) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Name, username)], "test"));

    private async Task<Participant> Join(string id)
    {
        var user = User(id, id);
        var form = (await service.DashboardAsync(user)).Person;
        form.Country = "US";
        form.Name = "Postal Name " + id;
        form.Line1 = "123 Encrypted Lane";
        form.Bio = "Coffee, books, and C#";
        form.Consent = true;
        await service.SaveParticipationAsync(user, form);

        return form;
    }

    [Fact]
    public async Task EncryptedFileRejectsPlaintextAndWrongPasswordAndSurvivesRestart()
    {
        await service.UnlockAsync(admin, Password, true);
        await Join("alice");
        var file = Path.Combine(directory, "santa.db");
        Assert.False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains("123 Encrypted Lane", StringComparison.Ordinal));
        Assert.NotEqual("SQLite format 3\0", Encoding.ASCII.GetString((await File.ReadAllBytesAsync(file))[..16]));

        await using (var plain = new SqliteConnection($"Data Source={file};Pooling=False"))
        {
            await plain.OpenAsync();
            await using var cmd = plain.CreateCommand();
            cmd.CommandText = "SELECT * FROM sqlite_master";
            await Assert.ThrowsAsync<SqliteException>(() => cmd.ExecuteScalarAsync());
        }

        var restarted = new DatabaseVault(configuration, environment);
        Assert.False(restarted.IsUnlocked);
        Assert.True(restarted.IsInitialized);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.UnlockAsync("incorrect", false));
        Assert.False(restarted.IsUnlocked);
        await restarted.UnlockAsync(Password, false);
        Assert.Equal("123 Encrypted Lane", await restarted.UseAsync(async db => (await db.Participants.SingleAsync()).Line1));
    }

    [Fact]
    public async Task AdminAccessUsesOneIdRegardlessOfUsername()
    {
        await service.UnlockAsync(admin, Password, true);
        Assert.True(service.IsAdmin(User("admin-id", "renamed-organizer")));
        await service.AdminAsync(User("admin-id", "renamed-organizer"));
        var impostor = User("other-id", "organizer");
        Assert.False(service.IsAdmin(impostor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AdminAsync(impostor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CopyDetailsAsync(impostor, "admin-id"));
        configuration["Admins:0"] = "organizer";
        configuration["AdminDiscordId"] = null;
        Assert.False(service.IsAdmin(admin));
        Assert.False(service.IsAdmin(impostor));
        configuration["AdminDiscordId"] = "admin-id,other-id";
        Assert.False(service.IsAdmin(admin));
        Assert.False(service.IsAdmin(impostor));
    }

    [Fact]
    public async Task OnlyConfiguredAdminCanUnlockOrReadAdminData()
    {
        var stranger = User("stranger", "someone");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UnlockAsync(stranger, Password, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AdminAsync(stranger));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CopyDetailsAsync(stranger, "admin-id"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DashboardAsync(stranger));
        await service.UnlockAsync(admin, Password, true);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DashboardAsync(new ClaimsPrincipal()));
    }

    [Fact]
    public async Task LockBoundaryIsEnforcedOnServerAndNewUsersCannotJoin()
    {
        await service.UnlockAsync(admin, Password, true);
        var form = await Join("alice");
        await service.ConfigureAsync(admin, new SantaCycle { LockInUtc = clock.Now.Date.AddDays(1) });
        clock.Now = clock.Now.Date.AddDays(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveParticipationAsync(User("alice", "alice"), form));
        var newcomer = (await service.DashboardAsync(User("new", "new"))).Person;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveParticipationAsync(User("new", "new"), newcomer));
    }

    [Fact]
    public async Task DrawPersistsShipmentIsPrivateAndRedrawResetsIt()
    {
        await service.UnlockAsync(admin, Password, true);
        await Join("alice");
        await Join("bob");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DrawAsync(admin));
        await service.ConfigureAsync(admin, new SantaCycle { LockInUtc = clock.Now.Date, DistributionUtc = clock.Now.Date.AddDays(1) });
        Assert.True((await service.DrawAsync(admin)).Complete);
        Assert.Equal(2, (await service.AdminAsync(admin)).Matches.Count);
        Assert.Null((await service.DashboardAsync(User("bob", "bob"))).Incoming);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveShipmentAsync(User("alice", "alice"), ShipmentStatus.Shipped, null));
        clock.Now = clock.Now.Date.AddDays(1);
        var eta = DateOnly.FromDateTime(clock.Now.AddDays(5));
        await service.SaveShipmentAsync(User("alice", "alice"), ShipmentStatus.Shipped, eta);
        var receiver = await service.DashboardAsync(User("bob", "bob"));
        Assert.Equal(ShipmentStatus.Shipped, receiver.Incoming!.Status);
        Assert.Equal(eta, receiver.Incoming.EstimatedArrival);
        Assert.DoesNotContain(typeof(ShipmentView).GetProperties(), p => p.Name.Contains("Id", StringComparison.Ordinal));
        await service.DrawAsync(admin);
        Assert.All((await service.AdminAsync(admin)).Matches, p =>
        {
            Assert.Equal(ShipmentStatus.Preparing, p.Status);
            Assert.Null(p.EstimatedArrival);
        });
    }

    [Fact]
    public async Task FailedDrawLeavesExistingAssignmentsIntact()
    {
        await service.UnlockAsync(admin, Password, true);
        await Join("alice");
        await Join("bob");
        await service.ConfigureAsync(admin, new SantaCycle { LockInUtc = clock.Now.Date });
        await service.DrawAsync(admin);
        var saved = (await service.AdminAsync(admin)).Matches.Select(p => p.Id).ToArray();
        await vault.UseAsync(async db =>
        {
            var p = await db.Participants.FindAsync("alice");
            p!.Send = false;
            await db.SaveChangesAsync();

            return true;
        });

        Assert.False((await service.DrawAsync(admin)).Complete);
        Assert.Equal(saved, (await service.AdminAsync(admin)).Matches.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task RemovalInvalidatesMatchesAndPreventsRejoiningUntilRestored()
    {
        await service.UnlockAsync(admin, Password, true);
        var form = await Join("alice");
        await Join("bob");
        await service.ConfigureAsync(admin, new SantaCycle { LockInUtc = clock.Now.Date });
        await service.DrawAsync(admin);
        await service.SetRemovedAsync(admin, "alice", true);
        Assert.Empty((await service.AdminAsync(admin)).Matches);
        await service.ConfigureAsync(admin, new SantaCycle());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveParticipationAsync(User("alice", "alice"), form));
        await service.SetRemovedAsync(admin, "alice", false);
        await service.SaveParticipationAsync(User("alice", "alice"), form);
    }

    [Fact]
    public async Task CopiesSelectedPersonsDetailsAndValidatesDateOrder()
    {
        await service.UnlockAsync(admin, Password, true);
        await Join("alice");
        await service.ConfigureAsync(admin, new SantaCycle { Greeting = "Hello from the workshop!" });
        var copy = await service.CopyDetailsAsync(admin, "alice");
        Assert.StartsWith("Hello from the workshop!", copy);
        Assert.Contains("@alice", copy);
        Assert.Contains("123 Encrypted Lane", copy);
        Assert.Contains("Coffee, books, and C#", copy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ConfigureAsync(admin, new SantaCycle { LockInUtc = clock.Now.AddDays(2), DistributionUtc = clock.Now }));
    }

    [Fact]
    public void FormRequiresAppropriateContactDetailsWithoutConsentCheckbox()
    {
        var p = new Participant { Country = "US", Name = "A", Line1 = "Street", Bio = "Coffee" };
        p.Validate();
        p.GiftPreference = GiftPreference.Digital;
        Assert.Throws<InvalidOperationException>(p.Validate);
        p.ContactNotes = "Steam username";
        p.Validate();
        p.Receive = false;
        p.Consent = false;
        p.Name = "";
        p.Line1 = "";
        p.Validate();
        Assert.Equal(GiftPreference.GiveOnly, p.GiftPreference);
        p.Send = false;
        Assert.Throws<InvalidOperationException>(p.Validate);
    }

    [Theory]
    [InlineData("North America")]
    [InlineData("South America")]
    [InlineData("Europe")]
    [InlineData("Asia")]
    [InlineData("Australia")]
    [InlineData("Other")]
    public void FormAcceptsRegions(string region)
    {
        var p = new Participant { Country = region, Receive = false };
        p.Validate();
        Assert.Equal(Countries.Regions[region], Countries.Name(region));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTime Now { get; set; } = new(2026, 12, 1, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private sealed class Host(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "SecretSanta";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
