using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;
using SecretSanta.Components;
using SecretSanta.Data;
using SecretSanta.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DatabaseVault>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Backup").Get<BackupOptions>() ?? new BackupOptions());
builder.Services.AddHostedService<DatabaseBackupService>();
builder.Services.AddSingleton<SantaService>();
builder.Services.AddAuthorization(options => options.AddPolicy("Admin", policy =>
    policy.RequireAuthenticatedUser().RequireClaim(ClaimTypes.NameIdentifier,
        builder.Configuration["AdminDiscordId"] is { Length: > 0 } adminId ? [adminId] : Array.Empty<string>())));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/login";
        options.AccessDeniedPath = "/";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Cookie.Name = "SecretCSharpies.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    })
    .AddOAuth("Discord", options =>
    {
        options.ClientId = builder.Configuration["Discord:ClientId"] ?? "";
        options.ClientSecret = builder.Configuration["Discord:ClientSecret"] ?? "";
        // Keep the unavailable landing page usable before Discord credentials are configured.
        if (string.IsNullOrWhiteSpace(options.ClientId))
            options.ClientId = "unconfigured";

        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            options.ClientSecret = "unconfigured";

        options.CallbackPath = "/signin-discord";
        options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
        options.TokenEndpoint = "https://discord.com/api/oauth2/token";
        options.UserInformationEndpoint = "https://discord.com/api/users/@me";
        options.Scope.Add("identify");
        options.SaveTokens = false;
        options.Events.OnCreatingTicket = async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
            using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var profile = document.RootElement;
            var id = profile.GetProperty("id").GetString()!;
            context.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, id));
            context.Identity.AddClaim(new Claim(ClaimTypes.Name, profile.GetProperty("username").GetString()!));
            if (profile.TryGetProperty("avatar", out var avatar) && avatar.ValueKind == JsonValueKind.String)
                context.Identity.AddClaim(new Claim("avatar", $"https://cdn.discordapp.com/avatars/{id}/{avatar.GetString()}.png?size=128"));

            var service = context.HttpContext.RequestServices.GetRequiredService<SantaService>();
            if (context.HttpContext.RequestServices.GetRequiredService<DatabaseVault>().IsUnlocked)
                await service.DashboardAsync(context.Principal!);
        };

        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/?login=failed");

            return Task.CompletedTask;
        };
    });

var app = builder.Build();

if (app.Configuration["Database:Password"] is { Length: > 0 } databasePassword)
{
    var vault = app.Services.GetRequiredService<DatabaseVault>();
    if (vault.IsInitialized)
        await vault.UnlockAsync(databasePassword, false);
}

// Linked artwork is copied to the output webroot; the development manifest otherwise looks only in the source webroot.
var outputWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(outputWebRoot))
    app.Environment.WebRootFileProvider = new CompositeFileProvider(app.Environment.WebRootFileProvider, new PhysicalFileProvider(outputWebRoot));

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", true);
    app.UseHsts();
}

// A configured canonical origin works behind a TLS proxy without trusting arbitrary forwarded headers.
if (!string.IsNullOrWhiteSpace(app.Configuration["PublicOrigin"]))
{
    var origin = new Uri(app.Configuration["PublicOrigin"]!);

    if (origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback))
        throw new InvalidOperationException("PublicOrigin must use HTTPS (HTTP is allowed only for localhost).");

    app.Use(async (context, next) =>
    {
        context.Request.Scheme = origin.Scheme;
        context.Request.Host = new HostString(origin.Authority);
        await next();
    });
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});

app.UseAuthentication();
if (app.Environment.IsDevelopment() && app.Configuration["HTTPS_PORT"] is not null)
    app.UseHttpsRedirection();

app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapGet("/health", () => Results.Ok(new { status = "running" }));
app.MapGet("/admin", () => Results.LocalRedirect("/?view=workshop")).RequireAuthorization("Admin");
app.MapGet("/auth/login", async (HttpContext context, IConfiguration configuration) =>
{
    if (string.IsNullOrWhiteSpace(configuration["Discord:ClientId"]) || string.IsNullOrWhiteSpace(configuration["Discord:ClientSecret"]))
    {
        context.Response.Redirect("/?login=unconfigured");

        return;
    }

    await context.ChallengeAsync("Discord", new AuthenticationProperties { RedirectUri = "/" });
});

app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest("Invalid request token.");

    await context.SignOutAsync();

    return Results.LocalRedirect("/");
}).RequireAuthorization();

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
