using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>In-memory IMemoryStore for config-command tests: settings dict + configure recording.</summary>
internal sealed class FakeConfigStore : IMemoryStore
{
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    public (string Provider, string? Model, string? BaseUrl)? Configured { get; private set; }

    public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();


    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
    public Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default)
    {
        Configured = (provider, model, baseUrl);
        Settings[EmbeddingSettingsKeys.Provider] = provider;
        if (model is null)
        {
            Settings.Remove(EmbeddingSettingsKeys.Model);
        }
        else
        {
            Settings[EmbeddingSettingsKeys.Model] = model;
        }

        if (baseUrl is null)
        {
            Settings.Remove(EmbeddingSettingsKeys.BaseUrl);
        }
        else
        {
            Settings[EmbeddingSettingsKeys.BaseUrl] = baseUrl;
        }

        Settings[EmbeddingSettingsKeys.Engine] = EmbeddingService.EngineFingerprint(provider, model, baseUrl);
        return Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled",
            EmbeddingService.EngineFingerprint(provider, model, baseUrl)));
    }

    public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            Settings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

    public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Settings.Remove(key);
        return Task.CompletedTask;
    }

    public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
