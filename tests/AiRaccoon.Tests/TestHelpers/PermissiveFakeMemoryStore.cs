using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     <see cref="FakeMemoryStore" /> whose members return benign defaults — empty results, zero counts,
///     "not found" — instead of throwing. Derive from it when the subject calls members only incidentally
///     (a guard reading a setting, a tool listing files) and the test's point lies in one or two overrides.
///     Derive from <see cref="FakeMemoryStore" /> instead when an unexpected call must fail the test.
///     Holds no state — behaviour belongs in the override.
/// </summary>
/// <remarks>
///     <see cref="IMemoryStore.StartModelMigrationAsync" /> and
///     <see cref="IMemoryStore.ReplaceIfFileChangedAsync" /> keep throwing: no default is benign for a
///     migration or a replace outcome.
/// </remarks>
public class PermissiveFakeMemoryStore : FakeMemoryStore
{
    public override Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MemoryEntry("h", "p.md", "project:test", "v", 1));

    public override Task<SearchResults> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new SearchResults([], SearchTimings.Empty));

    public override Task<int> DeleteAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(1);

    public override Task<MemoryEntry?> GetAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<MemoryEntry?>(null);

    public override Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public override Task<MemoryStats> GetStatsAsync(string projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MemoryStats(0, 0, []));

    public override Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MemoryEntryResult(
            new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1), true));

    public override Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>([]);

    public override Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SharedIndex([], []));

    public override Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public override Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult("{\"root\":\"\"}");

    public override Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(1);

    public override Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(1);

    public override Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EmbedPendingResult(0, 0));

    public override Task<MemoryEntryResult> AddContentAsync(string projectId, string path, string content,
        string? context, string? sourceFile = null, string? section = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new MemoryEntryResult(
            new MemoryEntry("new-hash", path, context ?? "project:test", content, 1), true));

    public override Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

    public override Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<EntryMetadata?>(null);

    public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public override Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public override Task ReplaceAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public override Task ReplaceAsync(string projectId, string path, string fileHash, string? context,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

    public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public override Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(true);
}
