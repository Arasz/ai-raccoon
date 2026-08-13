namespace AiRaccoon.Infrastructure.Sync;

public interface ISyncService
{
    /// <summary>Defaults <paramref name="objectKey" /> to "memory-{projectId}.db" when the caller has none configured — the naming convention is the service's concern, not the caller's.</summary>
    Task<SyncResult> MemorySyncAsync(string projectId, string? objectKey = null,
        CancellationToken cancellationToken = default);
}
