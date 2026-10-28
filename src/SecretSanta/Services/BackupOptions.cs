namespace SecretSanta.Services;

public sealed record BackupOptions
{
    public string Endpoint { get; init; } = "";
    public string Bucket { get; init; } = "";
    public string AccessKeyId { get; init; } = "";
    public string SecretAccessKey { get; init; } = "";
}
