namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Thrown when memory sync is requested without S3-compatible storage credentials.</summary>
public sealed class SyncNotConfiguredException() : InvalidOperationException(
    "Memory sync is not configured. Run 'ai-raccoon sync add s3 <url> --bucket <name>' and enter the credentials when prompted.");
