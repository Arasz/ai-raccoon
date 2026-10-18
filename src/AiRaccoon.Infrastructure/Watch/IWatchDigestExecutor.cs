using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Watch;

public interface IWatchDigestExecutor
{
    /// <summary>Digests one event; returns why the file was skipped, or null when it was indexed, unchanged, removed or excluded.</summary>
    Task<SkipReason?> DigestAsync(string projectId, string watchPath, string filePath, WatchEventKind kind,
        string? oldPath, CancellationToken cancellationToken = default);
}
