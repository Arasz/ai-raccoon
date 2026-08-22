using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>Null object for callers that construct <see cref="FileIngestor" /> without code-corpus
/// support (test/legacy positional call sites) — never matches a code extension.</summary>
public sealed class NullCodeFileTypeMatcher : ICodeFileTypeMatcher
{
    public static NullCodeFileTypeMatcher Instance { get; } = new();

    public bool IsCodeFile(string path) => false;
}

/// <summary>Null object for callers that construct <see cref="FileIngestor" /> without code-corpus
/// support — resolves to 0 chunks, ever.</summary>
public sealed class NullCodeIngestor : ICodeIngestor
{
    public static NullCodeIngestor Instance { get; } = new();

    public Task<CodeIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken, IReadOnlyList<string>? scope = null) => Task.FromResult(new CodeIngestResult(0, false));
}

/// <summary>Null object for callers that construct <see cref="FileIngestor" /> without watch-root
/// lookup (test/legacy positional call sites, or a bank with no watches) — resolves to no
/// registered watches, ever, so root resolution (B2) falls through to the ingest-scope allowlist.</summary>
public sealed class NullWatchStore : IWatchStore
{
    public static NullWatchStore Instance { get; } = new();

    public Task AddWatchAsync(string projectId, string path, long createdAt, long lastChangeTs,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RemoveWatchAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<WatchOverlapDecision> ResolveAndAddAsync(string projectId, WatchOverlapCandidate candidate,
        IWatchOverlapResolver overlapResolver, CancellationToken cancellationToken = default) =>
        Task.FromResult(new WatchOverlapDecision(WatchOverlapOutcome.Accepted, null, []));

    public Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WatchRegistration>>([]);

    public Task UpdateLastChangeAsync(string projectId, string path, long lastChangeTs,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<string?> GetFileHashAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task UpsertFileHashAsync(string projectId, string path, string fileHash, long updatedAt,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Null object for callers that construct <see cref="FileIngestor" /> without an embed
/// topic (test/legacy positional call sites) — every enqueue reports "dropped", never queues, and
/// nothing ever drains it. Production always supplies the real shared pump.</summary>
public sealed class NullEmbedDrainPump : IEventPump<EmbedDrainRequest>
{
    public static NullEmbedDrainPump Instance { get; } = new();

    public long EnqueuedCount => 0;

    public long DroppedCount => 0;

    public long CoalescedCount => 0;

    public bool TryEnqueue(EmbedDrainRequest item) => false;

    public IReadOnlyList<EmbedDrainRequest> DrainUpTo(int budget) => [];

    public Task WaitForItemAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);

    public void ApplyCapacity(int capacity)
    {
    }
}
