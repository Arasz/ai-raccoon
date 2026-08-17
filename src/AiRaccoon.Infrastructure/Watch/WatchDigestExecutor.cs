using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Replace-by-path digest: hash-skip (SHA-256 of normalized path + content), delete-by-source-path,
///     re-digest through the existing ingest path, rename = remove old path + digest new path with
///     overwrite (docs/plans/file-watcher-implementation.md D2). File gone → chunks removed.
/// </summary>
public sealed partial class WatchDigestExecutor(
    IMemoryStore store,
    IWatchStore watchStore,
    TimeProvider timeProvider,
    ILogger<WatchDigestExecutor> logger) : IWatchDigestExecutor
{
    public async Task DigestAsync(string projectId, string watchPath, string filePath, WatchEventKind kind,
        string? oldPath, CancellationToken cancellationToken = default)
    {
        var normalizedWatch = IngestPath.Normalize(watchPath);
        var normalized = IngestPath.Normalize(filePath);

        if (kind == WatchEventKind.Renamed && oldPath is not null)
        {
            await DeletePathAsync(projectId, normalizedWatch, IngestPath.Normalize(oldPath), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!File.Exists(normalized))
        {
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
