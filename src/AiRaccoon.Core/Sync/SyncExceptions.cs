namespace AiRaccoon.Core.Sync;

/// <summary>The remote snapshot changed while building our merge — caller must re-pull and retry.</summary>
public sealed class SyncConflictException(string message) : InvalidOperationException(message);

/// <summary>Network-level failure during sync push/pull.</summary>
public sealed class SyncNetworkException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>Integrity check failed on the remote snapshot; local DB is not replaced.</summary>
public sealed class SyncCorruptFileException(string message) : InvalidOperationException(message);

/// <summary>The remote snapshot's HMAC authenticity tag does not match its bytes; the snapshot
/// is refused before ATTACH and the local bank is not touched.</summary>
public sealed class SyncTamperedRemoteException(string message) : InvalidOperationException(message);

/// <summary>Credentials are missing or invalid.</summary>
public sealed class SyncAuthFailedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
///     The pull merged a <c>project_id_aliases</c> row whose alias the local bank already maps to a
///     different winner (Package E2): first-writer-wins keeps the local row, and the conflict
///     surfaces here — naming the alias and both winners — for a human to resolve, before the merge
///     mutates anything else. Mapped to <c>sync-alias-conflict</c>, never retried silently.
/// </summary>
public sealed class SyncAliasConflictException(string alias, string? localWinner, string? remoteWinner)
    : InvalidOperationException(
        $"Remote snapshot maps project id '{alias}' to " +
        $"'{(remoteWinner ?? "<dropped>")}', but this bank already maps it to '" +
        $"{(localWinner ?? "<dropped>")}'. Kept the local mapping and refused the merge — resolve " +
        "which winner is canonical (repair project-ids with the agreed map on one replica), then sync again.")
{
    public string Alias { get; } = alias;
    public string? LocalWinner { get; } = localWinner;
    public string? RemoteWinner { get; } = remoteWinner;
}

/// <summary>A sync.provider row was written with a value this build does not recognise.</summary>
public sealed class SyncProviderUnknownException(string written, string supported)
    : InvalidOperationException(
        $"sync.provider is set to '{written}', which is not a supported backend. Supported: {supported}. "
        + "An S3-compatible backend (MinIO, R2, Wasabi) is 's3' with its own endpoint: "
        + "'ai-raccoon settings sync add s3 <endpoint-url> --bucket <name>'. For Azure: 'ai-raccoon settings sync add azure <container>'.");
