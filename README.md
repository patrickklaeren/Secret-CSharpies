> [!NOTE]
> Look man I just wanted funny website, don't judge me based on these vibes.

# Secret CSharpies

A single-event Secret Santa for a C# Discord community. .NET 10 Blazor Interactive Server, Discord OAuth2 (`identify` only), EF Core and SQLCipher-encrypted SQLite. Aspire runs Santa as a project locally and publishes it through the Dockerfile with a persistent `/db` volume.

The app is one photo-backed page, without header/footer bars. While unavailable or signed out, it shows a centered 🎁 account control 65% up from the bottom, with an always-visible cursive sign-in/sign-out label beneath it. The participant form and admin workshop share the same background. `/?view=workshop` opens the admin view; `/admin` remains a protected redirect for existing links.

## Run through Aspire

Install the .NET 10 SDK and Aspire CLI. Docker is needed for container publishing, not the local project resource. From this repository:

```powershell
dotnet user-secrets set "Parameters:discord-client-id" "YOUR_DISCORD_CLIENT_ID" --project src/SecretSanta.AppHost
dotnet user-secrets set "Parameters:discord-client-secret" "YOUR_DISCORD_CLIENT_SECRET" --project src/SecretSanta.AppHost
dotnet user-secrets set "Parameters:admin-discord-id" "YOUR_DISCORD_USER_ID" --project src/SecretSanta.AppHost
dotnet user-secrets set "Parameters:database-password" "YOUR_EXISTING_DATABASE_PASSWORD" --project src/SecretSanta.AppHost
aspire start --apphost src/SecretSanta.AppHost/SecretSanta.AppHost.csproj
```

Open the Aspire dashboard URL printed by the host, or visit `https://localhost:7088`. The project uses its HTTPS launch profile and Aspire's automatic development-certificate configuration. In the [Discord Developer Portal](https://discord.com/developers/applications), create an application and add this exact OAuth2 redirect:

```text
https://localhost:7088/signin-discord
```

No bot, email scope, guild scope, or server membership checks are needed. Exactly one admin is configured by the permanent Discord user ID using `Parameters:admin-discord-id` in the AppHost or `AdminDiscordId` in the web container. Username changes do not affect access. The old `Admins` list and `admin-username` setting are ignored. A missing admin ID grants no admin access.

To announce first-time registrations, set the AppHost user secret `Parameters:discord-signup-webhook-url` to a Discord webhook URL. For standalone runs, use the web app user secret `Discord:SignupWebhookUrl`; in the deployed web container, use `Discord__SignupWebhookUrl`. Treat this URL as a secret. If it is absent, announcements are disabled. A failed announcement is logged and does not undo the registration; edits do not send another announcement.

Sign in as an admin to open the administration controls directly, then initialize the database using a password. Save the same password once as the AppHost user secret `Parameters:database-password` to automatically unlock the existing database on each restart. Aspire passes this secret as `Database__Password`. An empty or missing password keeps manual unlock available; an incorrect configured password fails startup without replacing the database. New databases still require explicit initialization in the admin controls. Keep the password safe; there is no recovery mechanism. The landing page and Discord login work while locked; event data and participation do not.

The local HTTPS launch profile sets `ASPNETCORE_ENVIRONMENT=Development`. Local data lives in `src/SecretSanta/App_Data`; this is separate from the Docker volume. The Docker image defaults to Production.

## Production hosting

Run one application instance, backed by the named `secret-csharpies-db` volume mounted at `/db`. Put the app behind an HTTPS reverse proxy with WebSocket support. Set the deployed web container's `PublicOrigin` environment variable to its HTTPS origin, for example `https://santa.example.com`, and register its `/signin-discord` redirect in Discord. Local Aspire runs do not need a public-origin parameter.

Production cookies require HTTPS. `PublicOrigin` is trusted deployment configuration: it sets the canonical host/scheme used by OAuth and redirects, without accepting arbitrary forwarded headers. Do not expose the plain HTTP app port directly to the public internet.

The Dockerfile uses a non-root `app` user and declares `VOLUME /db`. For bind mounts, make the directory writable by that user. Back up the whole database directory while the application is stopped, including any SQLite sidecar files. The active database key stays in process memory. If automatic unlock is configured, the password also resides in your local user-secrets store or deployed secret configuration. Password changes and database deletion are intentionally manual operations. Run only one replica: the unlock state and write serialization are process-local.

For a standalone image build (the normal launch path remains Aspire):

```powershell
docker build -t secret-csharpies .
```

## Encrypted R2 backups

The app checks for a backup on startup and every hour while the database is unlocked. It briefly pauses database operations, completes a WAL checkpoint, and reads the encrypted database into memory before releasing the lock. Uploading does not block participants. No plaintext copy or database password is written to the backup. This requires the documented single-instance deployment and no external database writers.

For Aspire, configure the optional AppHost parameters using user secrets:

```powershell
dotnet user-secrets set "Parameters:backup-endpoint" "https://YOUR_ACCOUNT_ID.r2.cloudflarestorage.com" --project src/SecretSanta.AppHost
dotnet user-secrets set "Parameters:backup-bucket" "YOUR_PRIVATE_BUCKET" --project src/SecretSanta.AppHost
dotnet user-secrets set "Parameters:backup-access-key-id" "YOUR_R2_ACCESS_KEY_ID" --project src/SecretSanta.AppHost
dotnet user-secrets set "Parameters:backup-secret-access-key" "YOUR_R2_SECRET_ACCESS_KEY" --project src/SecretSanta.AppHost
```

Both credential parameters are marked secret. All four parameters default to empty, keeping backups disabled until configured. Restart the exact AppHost after changing its parameter values. Aspire passes the parameters to the web app as the following environment variables, which can also be configured directly on a standalone web container:

```text
Backup__Endpoint=https://YOUR_ACCOUNT_ID.r2.cloudflarestorage.com
Backup__Bucket=YOUR_PRIVATE_BUCKET
Backup__AccessKeyId=YOUR_R2_ACCESS_KEY_ID
Backup__SecretAccessKey=YOUR_R2_SECRET_ACCESS_KEY
```

Use the S3 API endpoint shown by R2, including any jurisdiction suffix. Create an R2 Object Read & Write token restricted to this bucket. Keep the access keys in local user secrets or deployment secrets, never source control. Leave `Database__Password` unset for manual unlocking; backups use the already unlocked database and require no restart or password re-entry. Restart the web resource after changing backup settings. Missing settings disable backups; incomplete settings log an error.

SHA-256 is calculated over the encrypted snapshot. An unchanged hash skips the upload. The last successfully uploaded hash and destination are recorded as a fingerprint in `santa.db.r2-backup` beside the database, so restarts do not cause duplicate uploads. Changed snapshots are uploaded under dated `backups/*.db` keys, with the SHA-256 in the filename and object metadata. Upload failures preserve the previous fingerprint and retry on the next hourly run. Check application logs for success, skips, or failures. No backups are deleted automatically; do not expire every object by age, because an unchanged database may have only an older backup.

To recover after losing the VPS: download the newest successful snapshot, verify its SHA-256 against its filename, stop the replacement app, and place the snapshot in its database directory as `santa.db`. Use a fresh directory without old WAL/SHM files or a backup fingerprint. Start the app and manually unlock with your original database password. The app will back it up again on its next unlocked hourly check. During final teardown, delete the R2 backups as well as the VPS data. Hourly backups can lose up to an hour of changes; failed uploads extend that window.

## Event workflow

1. Set lock-in, distribution, and ship-by dates, and the copy-message greeting in Workshop. Dates are stored as midnight **UTC**; the site displays the boundary in the visitor's local timezone. A boundary can therefore display as the preceding local calendar day. Admin date inputs are explicitly UTC. An unset date does not activate its phase. Shipment ETA is a calendar date, not a timestamp.
2. Participants sign in, choose send, receive, or both, and submit their preferences, country, delivery information, and notes. Physical recipients provide a postal name/address; digital-only recipients provide delivery details in the notes. They can edit until lock-in. Admins can inspect all signed-in users, including incomplete sign-ups, and every submitted field. Use **Refresh** to fetch current progress.
3. After lock-in, draw names. Each sender gets one recipient and each recipient gets one sender, with no self-matches. Digital-only senders cannot match physical-only recipients. Regional ordering permits physical or digital delivery internationally. Domestic shipping is a **soft preference**: maximum coverage comes first, then the algorithm minimizes international physical-capable assignments for those senders. Equal-cost alternatives are randomized; this is not a uniformly sampled draw across all possible matchings.
4. If a complete draw is impossible, Workshop shows the maximum compatible partial pairing and the unmatched senders/recipients, with reasons. That preview is not saved and any previous complete draw remains intact. Adjust the roster/preferences before trying again. Forms can be reopened by moving/clearing lock-in only when there is no saved draw.
5. A successful draw is saved atomically. Click either name in an assignment to copy **that person's** contact information and notes with the greeting. Paste and send the information manually via Discord. The app does not send match details. If clipboard access is blocked, a selectable text area appears.
6. Redraw is allowed with confirmation. It replaces matches and resets shipment reports. Removing or restoring a participant also clears existing matches and shipment reports; redistribute any previously sent assignments after drawing again. Removal is retained against their Discord ID so signing in again cannot silently rejoin them.
7. Once distribution has passed, matched senders can update Preparing / Shipped / Delivered and an optional ETA. Their recipient sees the status and ETA without the Santa's identity. Admins can see shipment progress. Digital gifts can be marked Delivered immediately.

## Local development without Docker

This is useful for UI work while Docker is stopped. Aspire is still the supported container orchestration entry point.

```powershell
dotnet user-secrets set "Discord:ClientId" "YOUR_DISCORD_CLIENT_ID" --project src/SecretSanta
dotnet user-secrets set "Discord:ClientSecret" "YOUR_DISCORD_CLIENT_SECRET" --project src/SecretSanta
dotnet user-secrets set "AdminDiscordId" "YOUR_DISCORD_USER_ID" --project src/SecretSanta
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet dev-certs https --trust
dotnet run --project src/SecretSanta
```

Open `https://localhost:7088` and register `https://localhost:7088/signin-discord` for that local launch. The HTTPS launch profile redirects local HTTP requests on port 5088 to HTTPS. Without credentials, the snowy unavailable landing page still runs. Local encrypted data goes into `src/SecretSanta/App_Data` (ignored by Git). No authentication bypass or sample users are shipped.

## Build

```powershell
dotnet build SecretCSharpies.slnx
```

## Artwork and snowfall

The artwork lives in `src/SecretSanta/wwwroot/assets/` and is included automatically in the web app and Docker publish. A random Padoru starts a ten-second crossing every 6–14 seconds, allowing occasional overlapping passes. Right-to-left skaters are mirrored; spins and jumps have independent rare chances. Reduced-motion preferences disable snowfall and skating, and hidden tabs skip new skating passes.

The requested [CSS Snowfall Effect with Sass Generator](https://freefrontend.com/css-snow-effects/#2025-11-14-css-snowfall-effect-with-sass-generator-l) describes 350 flakes in three sizes with fall/rotation and flicker animations. This app recreates that documented effect with CSS variables and server-rendered flakes. The linked [CodePen source](https://codepen.io/anon/pen/YPKyMgp) returned HTTP 403 during implementation, so this is an adaptation, not a verbatim copy of its Sass. Asset ownership remains with the supplied assets' owners.

Encryption follows [Microsoft's SQLite encryption guidance](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption): the EF **Core** provider plus `SQLitePCLRaw.bundle_e_sqlcipher`, never the ordinary unencrypted SQLite bundle. Startup verifies `PRAGMA cipher_version` before creating or unlocking a database.

