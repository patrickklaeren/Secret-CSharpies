using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SecretSanta.Data;

namespace SecretSanta.Services;

public sealed class DatabaseBackupService(DatabaseVault vault, BackupOptions options,
    ILogger<DatabaseBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = new[] { options.Endpoint, options.Bucket, options.AccessKeyId, options.SecretAccessKey };

        if (settings.All(string.IsNullOrWhiteSpace))
        {
            logger.LogInformation("R2 backups are disabled: no Backup settings configured.");
            return;
        }

        if (settings.Any(string.IsNullOrWhiteSpace) ||
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            logger.LogError("R2 backups are disabled: configure Backup Endpoint (HTTPS origin), Bucket, AccessKeyId and SecretAccessKey.");
            return;
        }

        using var client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey), new AmazonS3Config
        {
            ServiceURL = uri.GetLeftPart(UriPartial.Authority),
            AuthenticationRegion = "auto",
            ForcePathStyle = true,
            Timeout = TimeSpan.FromMinutes(2),
            MaxErrorRetry = 2
        });
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        logger.LogInformation("R2 backups enabled: checking on startup and hourly while unlocked.");

        try
        {
            do
            {
                try
                {
                    await BackupAsync(client, uri, options.Bucket, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    // Do not log connection strings, credentials, or participant data.
                    logger.LogError("R2 backup failed ({ErrorType}, {Code}); will retry next hour. Previous backups are preserved.",
                        error.GetType().Name, (error as AmazonS3Exception)?.ErrorCode ?? "local-error");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task BackupAsync(IAmazonS3 client, Uri endpoint, string bucket, CancellationToken cancellationToken)
    {
        var snapshot = await vault.SnapshotAsync(cancellationToken);
        if (snapshot is null)
        {
            logger.LogInformation("R2 backup skipped: database is locked.");
            return;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(snapshot));
        // Include the destination so changing buckets always creates a first backup there.
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}\n{bucket}\n{hash}")));
        if (File.Exists(vault.BackupStatePath) &&
            await File.ReadAllTextAsync(vault.BackupStatePath, cancellationToken) == fingerprint)
        {
            logger.LogInformation("R2 backup skipped: database hash is unchanged since the last successful upload.");
            return;
        }

        var key = $"backups/{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}-{hash}.db";
        using var stream = new MemoryStream(snapshot, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/octet-stream",
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true,
            UseChunkEncoding = false
        };
        request.Metadata.Add("sha256", hash);
        await client.PutObjectAsync(request, cancellationToken);

        // A failed upload must never advance the hash. Atomic replacement survives interruptions.
        var temporaryState = vault.BackupStatePath + ".tmp";
        await File.WriteAllTextAsync(temporaryState, fingerprint, cancellationToken);
        File.Move(temporaryState, vault.BackupStatePath, overwrite: true);
        logger.LogInformation("Encrypted database backup uploaded to R2: {Key} ({Bytes} bytes).", key, snapshot.Length);
    }
}
