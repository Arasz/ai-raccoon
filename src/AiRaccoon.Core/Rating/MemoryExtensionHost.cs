using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Rating;

/// <summary>
///     Runs registered IMemoryExtension hooks in order around every store operation (spec §6.2).
///     The host implements IMemoryStore and decorates the real store, so hooks observe writes,
///     searches, deletes, sweeps and consolidations without the MCP layer knowing they exist.
/// </summary>
public sealed class MemoryExtensionHost(IMemoryStore inner, IEnumerable<IMemoryExtension> extensions)
    : IMemoryStore
{
    private readonly IReadOnlyList<IMemoryExtension> _extensions = [.. extensions];

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var extension in _extensions)
        {
            await extension.OnWriteAsync(
                new WriteContext(request.ProjectId, request.Content, request.Context, request.AgentId,
                    request.WorkspaceId),
                cancellationToken).ConfigureAwait(false);
        }

        return await inner.WriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        var results = await inner.SearchAsync(query, cancellationToken).ConfigureAwait(false);

        foreach (var extension in _extensions)
        {
            await extension.OnSearchAsync(
                new SearchContext(query.ProjectId, query.Query, results, query.WorkspaceId, query.Limit,
                    query.MinScore),
                cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    public async Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        foreach (var extension in _extensions)
        {
            await extension.OnDeleteAsync(new DeleteContext(projectId, hash), cancellationToken).ConfigureAwait(false);
        }

        return await inner.DeleteAsync(projectId, hash, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        foreach (var extension in _extensions)
        {
            await extension.OnDeleteAsync(new DeleteContext(projectId, Context: context), cancellationToken)
                .ConfigureAwait(false);
        }

        return await inner.DeleteContextAsync(projectId, context, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => await inner.GetStatsAsync(projectId, cancellationToken).ConfigureAwait(false);

    public async Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        await inner.ShareAsync(projectId, hash, cancellationToken).ConfigureAwait(false);

    public async Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => await inner.ListFilesAsync(projectId, cancellationToken).ConfigureAwait(false);

    public async Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        await inner.IngestFileAsync(projectId, path, context, cancellationToken).ConfigureAwait(false);

    public async Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        await inner.IngestDirectoryAsync(projectId, path, context, cancellationToken).ConfigureAwait(false);

    public async Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string model,
        string? apiKey, CancellationToken cancellationToken = default) =>
        await inner.ConfigureEmbeddingAsync(projectId, provider, model, apiKey, cancellationToken)
            .ConfigureAwait(false);

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default) =>
        await inner.EmbedPendingAsync(projectId, limit, cancellationToken).ConfigureAwait(false);

    public async Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
        CancellationToken cancellationToken = default) =>
        await inner.AddContentAsync(projectId, path, content, context, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        await inner.ListContextAsync(projectId, context, cancellationToken).ConfigureAwait(false);

    public async Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        await inner.GetMetadataAsync(projectId, hash, cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        await inner.GetSettingAsync(key, cancellationToken).ConfigureAwait(false);

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
        await inner.SetSettingAsync(key, value, cancellationToken).ConfigureAwait(false);

    public async Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default) =>
        await inner.SetEntryTtlAsync(projectId, hash, ttlDays, cancellationToken).ConfigureAwait(false);
}
