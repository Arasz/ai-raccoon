namespace AiRaccoon.Infrastructure.Sync;

/// <summary>The remote snapshot changed while building our merge — caller must re-pull and retry.</summary>
public sealed class SyncConflictException(string message) : InvalidOperationException(message);

/// <summary>Network-level failure during sync push/pull.</summary>
public sealed class SyncNetworkException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Integrity check failed on the remote snapshot; local DB is not replaced.</summary>
public sealed class SyncCorruptFileException(string message) : InvalidOperationException(message);

/// <summary>Credentials are missing or invalid.</summary>
public sealed class SyncAuthFailedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
