using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
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

    public Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken, IReadOnlyList<string>? scope = null) => Task.FromResult(0);
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

    public Task DeleteFileHashAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
