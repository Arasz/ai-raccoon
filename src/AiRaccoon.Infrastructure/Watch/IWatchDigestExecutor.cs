namespace AiRaccoon.Infrastructure.Watch;

public interface IWatchDigestExecutor
{
    Task DigestAsync(string projectId, string watchPath, string filePath, WatchEventKind kind,
        string? oldPath, CancellationToken cancellationToken = default);
}
