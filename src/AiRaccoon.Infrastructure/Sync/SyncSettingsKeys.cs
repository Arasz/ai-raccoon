namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Settings-table keys for cloud sync (written by `ai-raccoon settings sync add s3|azure`; read per memory_sync call).</summary>
public static class SyncSettingsKeys
{
    /// <summary>Active backend (s3 or azure); absent or unknown rows behave as s3 (docs/plans/azure-blob-sync-plan.md R2).</summary>
    public const string Provider = "sync.provider";

    public const string Endpoint = "sync.endpoint";
    public const string Bucket = "sync.bucket";
    public const string Region = "sync.region";
    public const string ObjectKey = "sync.objectKey";

    /// <summary>S3 access key, persisted in the settings table.</summary>
    public const string AccessKey = "sync.accessKey";

    /// <summary>S3 secret key, persisted in the settings table.</summary>
    public const string SecretKey = "sync.secretKey";

    /// <summary>Azure storage connection string, persisted in the settings table.</summary>
    public const string ConnectionString = "sync.connectionString";

    /// <summary>Azure blob container name.</summary>
    public const string Container = "sync.container";

    /// <summary>Azure storage account name for --cli mode (DefaultAzureCredential; non-secret).</summary>
    public const string AzureAccount = "sync.azureAccount";

    /// <summary>Marker row ("true") for s3 --cli mode (AWS default credential chain; non-secret).</summary>
    public const string S3Chain = "sync.s3Chain";
}
