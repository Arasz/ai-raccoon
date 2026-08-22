using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Ingestion;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Replace-by-path digest: hash-skip (SHA-256 of normalized path + content), delete-by-source-path,
///     re-digest through the existing ingest path, rename = remove old path + digest new path with
///     overwrite (docs/plans/file-watcher-implementation.md D2). File gone → chunks removed.
///     Excluded paths are never fingerprinted or chunked — stale chunks and any stale fingerprint
///     are removed instead: a hidden or deny-set segment below the watch root (ADR-0086 §6, the same
///     rule the catch-up enumeration applies) or an `ai-raccoon.ignore` match
///     (docs/work/2026-08-21-code-search-implementation-plan.md §2.1/§5.3). The ignore file itself is
///     never matched, and an edit to it (including its deletion) triggers a full re-scan of the watch.
/// </summary>
public sealed partial class WatchDigestExecutor(
    IMemoryStore store,
    IWatchStore watchStore,
    TimeProvider timeProvider,
    ILogger<WatchDigestExecutor> logger,
    IIgnoreRulesProvider ignoreRulesProvider,
    Lazy<IWatchScanInitiator> scanInitiator) : IWatchDigestExecutor
{
    public async Task DigestAsync(string projectId, string watchPath, string filePath, WatchEventKind kind,
        string? oldPath, CancellationToken cancellationToken = default)
    {
        var normalizedWatch = IngestPath.Normalize(watchPath);
        var normalized = IngestPath.Normalize(filePath);
        var isIgnoreFile = IsIgnoreFileItself(normalizedWatch, normalized);

        if (kind == WatchEventKind.Renamed && oldPath is not null)
        {
            await DeletePathAsync(projectId, normalizedWatch, IngestPath.Normalize(oldPath), cancellationToken)
                .ConfigureAwait(false);
        }

        if (Directory.Exists(normalized))
        {
            // A directory's own Created/Changed event: nothing to digest, and NOT a vanished file —
            // taking the delete cascade here would wipe the files inside it that a sibling digest in
            // the same batch just ingested (#509).
            return;
        }

        if (!File.Exists(normalized))
        {
            await DeletePathAsync(projectId, normalizedWatch, normalized, cancellationToken).ConfigureAwait(false);
            if (isIgnoreFile)
            {
                scanInitiator.Value.EnqueueInitialScan(projectId, normalizedWatch);
            }

            return;
        }

        if (!isIgnoreFile && await IsExcludedAsync(projectId, normalizedWatch, normalized, cancellationToken)
                .ConfigureAwait(false))
        {
            // Never fingerprinted (a later un-exclude must not hash-skip on unchanged content),
            // never chunked — only stale chunks from before the rule started matching are cleaned.
            // DeleteSourcePathAsync already cascades the fingerprint delete for this exact path
            // (MemorySql.DeleteWatchFilesByProjectPathCascade).
            await DeletePathAsync(projectId, normalizedWatch, normalized, cancellationToken).ConfigureAwait(false);
            return;
        }

        var content = await File.ReadAllTextAsync(normalized, cancellationToken).ConfigureAwait(false);
        var hash = ComputeHash(normalized, content);
        var previous = await watchStore.GetFileHashAsync(projectId, normalized, cancellationToken)
            .ConfigureAwait(false);
        if (string.Equals(previous, hash, StringComparison.Ordinal))
        {
            // Hash-skip: metadata-only touch — refresh timestamps, never touch memory or hooks.
            await TouchAsync(projectId, normalizedWatch, normalized, hash, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Delete + re-ingest + fingerprint are one transaction in the store: the pre-check above is
        // only a cheap filter, and a concurrent process either loses the race and skips or, on a
        // crash mid-digest, rolls back — the file is never left chunkless behind a matching hash.
        var replaced = await store.ReplaceIfFileChangedAsync(projectId, normalized, hash, cancellationToken)
            .ConfigureAwait(false);
        if (replaced)
        {
            await TryEmbedPendingAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        await watchStore.UpdateLastChangeAsync(projectId, normalizedWatch, Now(), cancellationToken)
            .ConfigureAwait(false);

        if (isIgnoreFile)
        {
            scanInitiator.Value.EnqueueInitialScan(projectId, normalizedWatch);
        }
    }

    private static bool IsIgnoreFileItself(string normalizedWatch, string normalizedPath) =>
        string.Equals(Path.GetFileName(normalizedPath), IgnoreRulesProvider.FileName, StringComparison.Ordinal) &&
        IngestPath.PathComparer.Equals(Path.GetDirectoryName(normalizedPath), normalizedWatch);

    /// <summary>The digest's gate: the enumeration's hidden/deny-set rule first (#494 — an event
    /// under an agent worktree must be skipped exactly like the walk skips it), then the watch
    /// root's `ai-raccoon.ignore` rules.</summary>
    private async Task<bool> IsExcludedAsync(string projectId, string normalizedWatch, string normalized,
        CancellationToken cancellationToken)
    {
        if (WatchDenySet.Excludes(normalizedWatch, normalized))
        {
            return true;
        }

        var ignoreRules = await ignoreRulesProvider.LoadAsync(normalizedWatch, cancellationToken)
            .ConfigureAwait(false);
        return ignoreRules.HasRules &&
               ignoreRules.IsIgnored(Path.GetRelativePath(normalizedWatch, normalized), isDirectory: false);
    }

    /// <summary>
    ///     Embeds the rows the replace transaction left pending — it defers embedding rather than
    ///     hold the bank's write lock through the engine, and nothing else retries a pending row.
    ///     A failure here is logged and never breaks the digest.
    /// </summary>
    private async Task TryEmbedPendingAsync(string projectId, CancellationToken cancellationToken)
    {
        try
        {
            await store.EmbedPendingAsync(projectId, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.EmbedFailed(logger, projectId, ex);
        }
    }

    /// <summary>SHA-256 over the normalized path concatenated with the full file content (docs/plans/file-watcher-implementation.md R5).</summary>
    public static string ComputeHash(string normalizedPath, string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath + content)));

    private async Task DeletePathAsync(string projectId, string watchPath, string path,
        CancellationToken cancellationToken)
    {
        await store.DeleteSourcePathAsync(projectId, path, cancellationToken).ConfigureAwait(false);
        await watchStore.UpdateLastChangeAsync(projectId, watchPath, Now(), cancellationToken).ConfigureAwait(false);
    }

    private async Task TouchAsync(string projectId, string watchPath, string path, string hash,
        CancellationToken cancellationToken)
    {
        await watchStore.UpsertFileHashAsync(projectId, path, hash, Now(), cancellationToken).ConfigureAwait(false);
        await watchStore.UpdateLastChangeAsync(projectId, watchPath, Now(), cancellationToken).ConfigureAwait(false);
    }

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private static partial class Log
    {
        [LoggerMessage(EventId = 400, Level = LogLevel.Warning,
            Message = "Best-effort embed after watch ingest failed for {ProjectId}; rows stay pending")]
        public static partial void EmbedFailed(ILogger logger, string projectId, Exception exception);
    }
}
