namespace AiRaccoon.Infrastructure.Options;

/// <summary>Cloud sync backend selected by the sync.provider setting row (default s3).</summary>
public enum SyncProvider
{
    S3,
    Azure
}

/// <summary>Parses a sync.provider row; absent or unknown values behave as S3 (docs/plans/azure-blob-sync-plan.md R2).</summary>
public static class SyncProviderParser
{
    public static SyncProvider Parse(string? value) =>
        Enum.TryParse<SyncProvider>(value, true, out var provider) && Enum.IsDefined(provider)
            ? provider
            : SyncProvider.S3;
}

/// <summary>Sync settings resolved per memory_sync call from the sync.* settings rows.</summary>
public sealed record SyncOptions
{
    public SyncProvider Provider { get; init; } = SyncProvider.S3;
    public string? Endpoint { get; init; }
    public string? Bucket { get; init; }
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public string? Region { get; init; }
    public string? ObjectKey { get; init; }
    public string? ConnectionString { get; init; }
    public string? Container { get; init; }

    /// <summary>Azure storage account name for the DefaultAzureCredential (--cli) mode; non-secret.</summary>
    public string? Account { get; init; }

    /// <summary>True when s3 uses the AWS default credential chain (--cli mode) instead of persisted keys.</summary>
    public bool S3Chain { get; init; }

    /// <summary>
    ///     True when the provider's rows are complete. When both credential modes are present
    ///     (manual settings edits) the tie-break is deterministic: connection string wins for
    ///     azure, persisted keys win for s3 — documented, not an error.
    /// </summary>
    public bool IsConfigured =>
        Provider switch
        {
            SyncProvider.Azure => (!string.IsNullOrWhiteSpace(ConnectionString) || !string.IsNullOrWhiteSpace(Account))
                                  && !string.IsNullOrWhiteSpace(Container),
            _ => !string.IsNullOrWhiteSpace(Endpoint)
                 && !string.IsNullOrWhiteSpace(Bucket)
                 && (!string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey) || S3Chain)
        };
}
