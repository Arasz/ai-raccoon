using System.Security.Cryptography;
using System.Text;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Replace-by-path digest: hash-skip (SHA-256 of normalized path + content), delete-by-source-path,
///     OnSourceChanged firing (post-hash-skip, pre-ingest), re-digest through the existing ingest path,
///     rename = remove old path + digest new path with overwrite (D2). File gone → chunks removed.
/// </summary>
public sealed class WatchDigestExecutor(
    IMemoryStore store,
    IWatchStore watchStore,
    MemoryExtensionHost extensionHost,
    TimeProvider timeProvider)
{
    public async Task DigestAsync(string projectId, string watchPath, string filePath, WatchEventKind kind,
        string? oldPath, CancellationToken cancellationToken = default)
    {
        var normalizedWatch = WatchPath.Normalize(watchPath);
        var normalized = WatchPath.Normalize(filePath);

        if (kind == WatchEventKind.Renamed && oldPath is not null)
        {
            await DeletePathAsync(projectId, normalizedWatch, WatchPath.Normalize(oldPath), cancellationToken)
                .ConfigureAwait(false);
        }

        if (!File.Exists(normalized))
        {
            await extensionHost.OnSourceChangedAsync(
                    new SourceChangedContext(projectId, normalized, SourceChangeKind.Deleted), cancellationToken)
                .ConfigureAwait(false);
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

        await extensionHost.OnSourceChangedAsync(new SourceChangedContext(projectId, normalized, ToKind(kind)),
                cancellationToken)
            .ConfigureAwait(false);
        await DeletePathAsync(projectId, normalizedWatch, normalized, cancellationToken).ConfigureAwait(false);
        await store.IngestFileAsync(projectId, normalized, null, cancellationToken).ConfigureAwait(false);
        await TouchAsync(projectId, normalizedWatch, normalized, hash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>SHA-256 over the normalized path concatenated with the full file content (R5 contract).</summary>
    public static string ComputeHash(string normalizedPath, string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath + content)));

    private static SourceChangeKind ToKind(WatchEventKind kind) =>
        kind switch
        {
            WatchEventKind.Created => SourceChangeKind.Created,
            WatchEventKind.Changed => SourceChangeKind.Changed,
            WatchEventKind.Deleted => SourceChangeKind.Deleted,
            WatchEventKind.Renamed => SourceChangeKind.Renamed,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

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
