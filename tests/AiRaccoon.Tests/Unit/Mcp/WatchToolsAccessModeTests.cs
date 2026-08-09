using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Access-tier gating at the watch tool boundary (docs/plans/file-watcher-implementation.md
///     D7/D8): add/remove need rw+, status answers in every tier. Mirrors
///     MemoryToolsAccessModeTests setup.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchToolsAccessModeTests
{
    private readonly FakeStore _store = new();
    private readonly WatchTools _tools;

    public WatchToolsAccessModeTests()
    {
        _tools = new WatchTools(new FakeWatchService(),
            new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue()), new ToolCallMetrics());
    }

    private void SetMode(string? global = null, string? perProject = null)
    {
        if (global is not null)
        {
            _store.Settings[AccessModePolicy.GlobalSettingKey] = global;
        }

        if (perProject is not null)
        {
            _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = perProject;
        }
    }

    [Fact]
    public async Task RoMode_AddIsDenied()
    {
        SetMode(perProject: "ro");

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _tools.Add("acme-web", "/repo", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("memory_watch_add requires mode rw (current ro)");
    }

    [Fact]
    public async Task RoMode_RemoveIsDenied()
    {
        SetMode(perProject: "ro");

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _tools.Remove("acme-web", "/repo", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("memory_watch_remove requires mode rw (current ro)");
    }

    [Fact]
    public async Task RoMode_StatusIsAllowed()
    {
        SetMode(perProject: "ro");

        var result = await _tools.Status("acme-web", TestContext.Current.CancellationToken);

        result.Data!.Watches.ShouldBeEmpty();
    }

    [Fact]
    public async Task DefaultRwMode_AddAndRemoveSucceed()
    {
        var added = await _tools.Add("acme-web", "/repo", TestContext.Current.CancellationToken);
        var removed = await _tools.Remove("acme-web", "/repo", TestContext.Current.CancellationToken);

        added.ShouldNotBeNull();
        removed.ShouldNotBeNull();
    }

    [Fact]
    public async Task FullMode_AddAndRemoveSucceed()
    {
        SetMode(perProject: "full");

        var added = await _tools.Add("acme-web", "/repo", TestContext.Current.CancellationToken);
        var removed = await _tools.Remove("acme-web", "/repo", TestContext.Current.CancellationToken);

        added.ShouldNotBeNull();
        removed.ShouldNotBeNull();
    }

    private sealed class FakeWatchService : IWatchService
    {
        public Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchStatus>>([]);

        public Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsPathAllowedAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("h", "p.md", "project:acme-web", "v", 0));

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryStats(0, 0, []));



    public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1));

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult("{\"root\":\"\"}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", provider == "local" ? "local" : "remote"));

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Settings.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Settings
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("new-hash", path, context ?? "project:acme-web", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(null);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
