using AiRaccoon.Core.Sync;

namespace AiRaccoon.Infrastructure.Options;

/// <summary>Cloud sync backend selected by the sync.provider setting row (default s3).</summary>
public enum SyncProvider
{
    S3,
    Azure
}

/// <summary>
///     Parses a sync.provider row. Absent means the documented S3 default
///     (docs/plans/azure-blob-sync-plan.md R2); a value that was written but is not recognised
///     throws rather than silently resolving to S3 (architect finding A-F8).
/// </summary>
public static class SyncProviderParser
{
    private const string Supported = "s3, azure";

    public static SyncProvider Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SyncProvider.S3;
        }

        return TryParse(value, out var provider)
            ? provider
            : throw new SyncProviderUnknownException(value.Trim(), Supported);
    }

    /// <summary>
    ///     Non-throwing form for diagnostics. `sync show` must report an unrecognised row rather
    ///     than crash — it is the command an operator runs *to find* the typo.
    /// </summary>
    public static bool TryParse(string? value, out SyncProvider provider)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            provider = SyncProvider.S3;
            return true;
        }

        return Enum.TryParse(value.Trim(), true, out provider) && Enum.IsDefined(provider);
    }
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
