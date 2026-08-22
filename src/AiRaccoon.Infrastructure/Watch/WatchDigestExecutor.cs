using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Replace-by-path digest: hash-skip (SHA-256 of normalized path + content), delete-by-source-path,
///     re-digest through the existing ingest path, rename = remove old path + digest new path with
///     overwrite (docs/plans/file-watcher-implementation.md D2). File gone → chunks removed.
///     `ai-raccoon.ignore` (docs/work/2026-08-21-code-search-implementation-plan.md §2.1/§5.3): an
///     ignored path is never fingerprinted or chunked — stale chunks and any stale fingerprint are
///     removed instead; the ignore file itself is never matched against its own rules, and an edit
///     to it (including its deletion) triggers a full re-scan of the watch.
/// </summary>
public sealed class WatchDigestExecutor(
    IMemoryStore store,
    IWatchStore watchStore,
    TimeProvider timeProvider,
    IIgnoreRulesProvider ignoreRulesProvider,
    Lazy<IWatchScanInitiator> scanInitiator,
    IEventPump<EmbedDrainRequest> embedDrainPump) : IWatchDigestExecutor
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

        if (!File.Exists(normalized))
        {
            await DeletePathAsync(projectId, normalizedWatch, normalized, cancellationToken).ConfigureAwait(false);
            if (isIgnoreFile)
            {
                scanInitiator.Value.EnqueueInitialScan(projectId, normalizedWatch);
            }

            return;
        }

        if (!isIgnoreFile)
        {
            var ignoreRules = await ignoreRulesProvider.LoadAsync(normalizedWatch, cancellationToken)
                .ConfigureAwait(false);
            if (ignoreRules.HasRules && IsIgnored(ignoreRules, normalizedWatch, normalized))
            {
                // Never fingerprinted (a later un-ignore must not hash-skip on unchanged content),
                // never chunked — only stale chunks from before the ignore rule existed are cleaned.
                // DeleteSourcePathAsync already cascades the fingerprint delete for this exact path
                // (MemorySql.DeleteWatchFilesByProjectPathCascade) — a second, separate
                // DeleteFileHashAsync call here was a dead no-op repeating the same row delete.
                await store.DeleteSourcePathAsync(projectId, normalized, cancellationToken).ConfigureAwait(false);
                await watchStore.UpdateLastChangeAsync(projectId, normalizedWatch, Now(), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
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
            // WP11-B2: signal the embed topic instead of draining inline and unbounded — the rows
            // stay embed_state='pending' (the durable outbox, ADR-0076) until EmbedDrainService's
            // single reader gets to them.
            embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory));
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

    private static bool IsIgnored(IgnoreRules rules, string root, string path) =>
        rules.IsIgnored(Path.GetRelativePath(root, path), isDirectory: false);

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
}
