namespace AiRaccoon.Core.Sync;

/// <summary>The remote snapshot changed while building our merge — caller must re-pull and retry.</summary>
public sealed class SyncConflictException(string message) : InvalidOperationException(message);

/// <summary>Network-level failure during sync push/pull.</summary>
public sealed class SyncNetworkException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Integrity check failed on the remote snapshot; local DB is not replaced.</summary>
public sealed class SyncCorruptFileException(string message) : InvalidOperationException(message);

/// <summary>Credentials are missing or invalid.</summary>
public sealed class SyncAuthFailedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>A sync.provider row was written with a value this build does not recognise.</summary>
public sealed class SyncProviderUnknownException(string written, string supported)
    : InvalidOperationException(
        $"sync.provider is set to '{written}', which is not a supported backend. Supported: {supported}. "
        + "An S3-compatible backend (MinIO, R2, Wasabi) is 's3' with its own endpoint: "
        + "'ai-raccoon sync add s3 <endpoint-url> --bucket <name>'. For Azure: 'ai-raccoon sync add azure <container>'.");
