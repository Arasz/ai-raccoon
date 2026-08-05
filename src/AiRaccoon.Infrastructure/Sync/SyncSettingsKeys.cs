namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Settings-table keys for cloud sync (written by `ai-raccoon sync add s3|azure`; read per memory_sync call).</summary>
public static class SyncSettingsKeys
{
    /// <summary>Active backend (s3 or azure); absent or unknown rows behave as s3 (ruling R2).</summary>
    public const string Provider = "sync.provider";
    public const string Endpoint = "sync.endpoint";
    public const string Bucket = "sync.bucket";
    public const string Region = "sync.region";
    public const string ObjectKey = "sync.objectKey";

    /// <summary>S3 access key, persisted in the settings table (single-channel ruling 2026-08-04).</summary>
    public const string AccessKey = "sync.accessKey";

    /// <summary>S3 secret key, persisted in the settings table (single-channel ruling 2026-08-04).</summary>
    public const string SecretKey = "sync.secretKey";

    /// <summary>Azure storage connection string, persisted in the settings table (single-channel ruling 2026-08-04).</summary>
    public const string ConnectionString = "sync.connectionString";

    /// <summary>Azure blob container name.</summary>
    public const string Container = "sync.container";
}
