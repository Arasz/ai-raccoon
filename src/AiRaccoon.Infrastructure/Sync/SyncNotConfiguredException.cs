namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Thrown when memory sync is requested without S3-compatible storage credentials.</summary>
public sealed class SyncNotConfiguredException() : InvalidOperationException(
    "Memory sync is not configured. Set AIRACCOON_SYNC_ENDPOINT, AIRACCOON_SYNC_BUCKET, " +
    "AIRACCOON_SYNC_ACCESS_KEY and AIRACCOON_SYNC_SECRET_KEY.");
