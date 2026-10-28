using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SecretSanta.Data;
using SecretSanta.Services;
using Xunit;

namespace SecretSanta.Tests;

public sealed class WebTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "santa-web-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ConfiguredPasswordUnlocksExistingDatabaseAfterRestart()
    {
        WebApplicationFactory<Program> CreateApp(string? password) => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Development")
                .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:Directory"] = directory,
                    ["Database:Password"] = password
                })));

        await using (var first = CreateApp(null))
        {
            using var client = first.CreateClient();
            await first.Services.GetRequiredService<DatabaseVault>().UnlockAsync("startup-test-password", true);
        }

        await using (var restarted = CreateApp("startup-test-password"))
        {
            using var client = restarted.CreateClient();
            Assert.True(restarted.Services.GetRequiredService<DatabaseVault>().IsUnlocked);
        }

        await using (var wrongPassword = CreateApp("incorrect-password"))
        {
            Assert.Throws<InvalidOperationException>(() => wrongPassword.CreateClient());
        }

        await using (var manual = CreateApp(null))
        {
            using var client = manual.CreateClient();
            var vault = manual.Services.GetRequiredService<DatabaseVault>();
            Assert.False(vault.IsUnlocked);
            await vault.UnlockAsync("startup-test-password", false);
        }
    }

    [Fact]
    public async Task LockedLandingWorksAndAdminCannotBeAccessedAnonymously()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder
            => builder.UseEnvironment("Development").ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory })));

        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("class=\"present-button\"", html);
        Assert.Contains("aria-label=\"Sign in with Discord\"", html);
        Assert.DoesNotContain("workshop is warming up", html);
        Assert.DoesNotContain("floating-controls", html);
        Assert.DoesNotContain("Discord sign-in did not finish", await client.GetStringAsync("/?login=failed"));
        Assert.DoesNotContain("<header", html);
        Assert.DoesNotContain("<footer", html);
        Assert.Equal(350, html.Split("class=\"snowflake\"").Length - 1);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/auth/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        var artwork = await client.GetAsync("/assets/christmas-background.webp");
        Assert.Equal("image/webp", artwork.Content.Headers.ContentType?.MediaType);
        Assert.True((await artwork.Content.ReadAsByteArrayAsync()).Length > 1_000_000);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/auth/logout")).StatusCode);
    }

    [Fact]
    public async Task AdminRouteRequiresTheSingleConfiguredId()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = directory, ["AdminDiscordId"] = "admin-id", ["Admins:0"] = "organizer"
            }))
            .ConfigureTestServices(services => services.AddAuthentication(options => options.DefaultAuthenticateScheme = "Test")
                .AddScheme<AuthenticationSchemeOptions, TestIdentity>("Test", _ => { })));
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "renamed-organizer");
        client.DefaultRequestHeaders.Add("X-Test-Id", "admin-id");
        Assert.Equal("/?view=workshop", (await client.GetAsync("/admin")).Headers.Location!.OriginalString);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "organizer");
        client.DefaultRequestHeaders.Remove("X-Test-Id");
        client.DefaultRequestHeaders.Add("X-Test-Id", "other-id");
        Assert.Equal("/", (await client.GetAsync("/admin")).Headers.Location!.AbsolutePath);
        Assert.DoesNotContain("Create encrypted database", await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task AdminAndParticipantPagesRenderWithPrivateDataSeparated()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development")
            .ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Directory"] = directory, ["AdminDiscordId"] = "admin-id" }))
            .ConfigureTestServices(services => services.AddAuthentication(options => options.DefaultAuthenticateScheme = "Test")
                .AddScheme<AuthenticationSchemeOptions, TestIdentity>("Test", _ => { })));

        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-Test-User", "organizer");
        var setup = await client.GetStringAsync("/");
        Assert.Contains("Create encrypted database", setup);
        Assert.DoesNotContain("The workshop", setup);
        Assert.DoesNotContain("Let’s start a little tradition", setup);
        Assert.DoesNotContain("Refresh", setup);
        var santa = app.Services.GetRequiredService<SantaService>();
        await santa.UnlockAsync(TestIdentity.Principal("organizer"), "integration-test-password", true);
        var alice = TestIdentity.Principal("alice");
        var form = (await santa.DashboardAsync(alice)).Person;
        form.Country = "US";
        form.Name = "Alice Private Postal Name";
        form.Line1 = "123 Private Test Street";
        form.Bio = "Books and coffee";
        form.Consent = true;
        await santa.SaveParticipationAsync(alice, form);
        var adminHtml = await client.GetStringAsync("/");
        Assert.Contains("Alice Private Postal Name", adminHtml);
        Assert.Contains("Forms submitted", adminHtml);
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "bob");
        var participantHtml = await client.GetStringAsync("/");
        Assert.Contains("Choose your region", participantHtml);
        Assert.DoesNotContain("Alice Private Postal Name", participantHtml);
        var denied = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Equal("/", denied.Headers.Location!.AbsolutePath);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/auth/logout", new StringContent(""))).StatusCode);
    }

    private sealed class TestIdentity(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public static ClaimsPrincipal Principal(string name, string? id = null) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id ?? (name == "organizer" ? "admin-id" : name)), new Claim(ClaimTypes.Name, name)], "Test"));

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.TryGetValue("X-Test-User", out var name)
                ? AuthenticateResult.Success(new AuthenticationTicket(Principal(name.ToString(), Request.Headers["X-Test-Id"].FirstOrDefault()), "Test"))
                : AuthenticateResult.NoResult());
    }
}
