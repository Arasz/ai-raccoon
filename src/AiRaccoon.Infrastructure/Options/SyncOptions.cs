namespace AiRaccoon.Infrastructure.Options;

/// <summary>Sync settings resolved per memory_sync call from the sync.* settings rows.</summary>
public sealed record SyncOptions
{
    public string? Endpoint { get; init; }
    public string? Bucket { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public string? Region { get; init; }
    public string? ObjectKey { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Endpoint)
        && !string.IsNullOrWhiteSpace(Bucket)
        && !string.IsNullOrWhiteSpace(AccessKey)
        && !string.IsNullOrWhiteSpace(SecretKey);
}
