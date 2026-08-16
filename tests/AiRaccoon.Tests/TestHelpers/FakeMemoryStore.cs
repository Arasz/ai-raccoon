using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     Base fake for <see cref="IMemoryStore" />: every member throws until a test overrides it, so a
///     fake declares only the members its subject calls. Holds no state — behaviour belongs in the override.
///     <see cref="GetSettingsByPrefixAsync" /> is the one exception: it defaults to an empty dictionary
///     instead of throwing, so a subject that batches settings reads does not force every fake that only
///     cares about <see cref="GetSettingAsync" />/<see cref="SetSettingAsync" /> to also override it. A
///     fake that needs the batched read to see seeded settings must still override it itself.
/// </summary>
/// <remarks>
///     <see cref="IMemoryStore.DeleteInScopeAsync" /> is declared here and forwards to
///     <see cref="DeleteAsync" />, so overriding that member still covers both. It used to inherit
///     that forwarding from the interface's own default — which widened a scoped delete into an
///     unscoped one for every implementor, not just this fake (ADR-0054). Simplifying is a fake's
///     privilege; it was never the port's.
/// </remarks>
public class FakeMemoryStore : IMemoryStore, ISettingsStore
{
    public virtual Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(WriteAsync));

    public virtual Task<SearchResults> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(SearchAsync));

    public virtual Task<bool> DeleteAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(DeleteAsync));

    /// <summary>Forwards to <see cref="DeleteAsync" /> — see the remarks on this type.</summary>
    public virtual Task<bool> DeleteInScopeAsync(string projectId, string hash, string scope,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(projectId, hash, cancellationToken);

    /// <summary>
    ///     Declared virtual here rather than left to IMemoryStore's default implementation: a
    ///     derived fake's own GetAsync would not take part in interface dispatch, so it would
    ///     silently return "not found" (ADR-0035).
    /// </summary>
    public virtual Task<MemoryEntry?> GetAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(GetAsync));

    public virtual Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(DeleteContextAsync));

    public virtual Task<MemoryStats> GetStatsAsync(string projectId,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(GetStatsAsync));

    public virtual Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(ShareAsync));

    public virtual Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(ExtractCandidatesAsync));

    public virtual Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(GetSharedIndexAsync));

    public virtual Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(GetProjectIdsAsync));

    public virtual Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(ListFilesAsync));

    public virtual Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(IngestFileAsync));

    public virtual Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(IngestDirectoryAsync));

    public virtual Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(ConfigureEmbeddingAsync));

    public virtual Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(EmbedPendingAsync));

    public virtual Task<MemoryEntryResult> AddContentAsync(string projectId, string path, string content,
        string? context, string? sourceFile = null, string? section = null,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(AddContentAsync));

    public virtual Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(ListContextAsync));

    public virtual Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(GetMetadataAsync));

    public virtual Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(GetSettingAsync));

    public virtual Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(SetSettingAsync));

    public virtual Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(DeleteSourcePathAsync));

    public virtual Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(ReplaceFileAsync));

    public virtual Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));

    public virtual Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(DeleteSettingAsync));

    public virtual Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default) =>
        throw NotOverridden(nameof(SetEntryTtlAsync));

    private static NotSupportedException NotOverridden(string member) =>
        new($"{nameof(FakeMemoryStore)}.{member} is not overridden by this test's fake.");
}
