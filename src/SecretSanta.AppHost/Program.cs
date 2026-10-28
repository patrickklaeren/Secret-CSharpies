using Projects;

var builder = DistributedApplication.CreateBuilder(args);
var clientId = builder.AddParameter("discord-client-id", true);
var clientSecret = builder.AddParameter("discord-client-secret", true);
var adminDiscordId = builder.AddParameter("admin-discord-id");
var databasePassword = builder.AddParameter("database-password", "", secret: true);
var backupEndpoint = builder.AddParameter("backup-endpoint", "");
var backupBucket = builder.AddParameter("backup-bucket", "");
var backupAccessKeyId = builder.AddParameter("backup-access-key-id", "", secret: true);
var backupSecretAccessKey = builder.AddParameter("backup-secret-access-key", "", secret: true);

builder.AddProject<SecretSanta>("secret-santa", "https")
    .WithEnvironment("Discord__ClientId", clientId)
    .WithEnvironment("Discord__ClientSecret", clientSecret)
    .WithEnvironment("AdminDiscordId", adminDiscordId)
    .WithEnvironment("Database__Password", databasePassword)
    .WithEnvironment("Backup__Endpoint", backupEndpoint)
    .WithEnvironment("Backup__Bucket", backupBucket)
    .WithEnvironment("Backup__AccessKeyId", backupAccessKeyId)
    .WithEnvironment("Backup__SecretAccessKey", backupSecretAccessKey)
    .WithHttpHealthCheck("/health")
    .PublishAsDockerFile(container => container
        .WithDockerfile("../..")
        .WithVolume("secret-csharpies-db", "/db")
        .WithEnvironment("Database__Directory", "/db"));

builder.Build().Run();
