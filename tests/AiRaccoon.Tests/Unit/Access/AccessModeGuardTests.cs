using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Access;

/// <summary>
///     Guard resolution/enforcement and the forgetting-knob gate (FR-NM-2 scenarios 5-6; see docs/work/features-native-memory/native-memory.feature): knob
///     adjustments are destructive, denied in rw and applied in full.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AccessModeGuardTests
{
    private readonly MemoryAccessGuard _guard;
    private readonly ForgettingPolicyService _knobs;
    private readonly FakeStore _store = new();

    public AccessModeGuardTests()
    {
        _guard = new MemoryAccessGuard(_store);
        _knobs = new ForgettingPolicyService(_store, _guard);
    }

    [Fact]
    public async Task Resolve_PerProjectOverride_BeatsGlobalDefault()
    {
        _store.Settings[AccessModePolicy.GlobalSettingKey] = "ro";
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full";

        (await _guard.ResolveAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(AccessMode.Full);
    }

    [Fact]
    public async Task Resolve_NoPerProject_UsesGlobalDefault()
    {
        _store.Settings[AccessModePolicy.GlobalSettingKey] = "ro";

        (await _guard.ResolveAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(AccessMode.Ro);
    }

    [Fact]
    public async Task Resolve_NothingConfigured_DefaultsToRw() => (await _guard.ResolveAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(AccessMode.Rw);

    [Fact]
    public async Task Ensure_Denied_ThrowsAccessDeniedWithRequiredAndCurrentMode()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "rw";

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _guard.EnsureAsync("acme", AccessRequirement.Destructive, "memory_delete",
                TestContext.Current.CancellationToken));

        // The "access-denied:" wire prefix is added by the CallToolFilter (ToolRefusalsTests), not the guard itself.
        ex.Message.ShouldBe("memory_delete requires mode full (current rw)");
    }

    // Scenario 5: forgetting knobs denied in rw, policy unchanged.
    [Fact]
    public async Task RwMode_SweepThresholdAdjustment_IsDeniedAndPolicyUnchanged()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = "rw";

        await Should.ThrowAsync<AccessDeniedException>(() =>
            _knobs.SetSweepThresholdAsync("acme-web", 0.1, TestContext.Current.CancellationToken));

        (await _knobs.GetSweepThresholdAsync("acme-web", TestContext.Current.CancellationToken)).ShouldBe(0.3);
    }

    [Fact]
    public async Task RwMode_EntryTtlAdjustment_IsDeniedAndPolicyUnchanged()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = "rw";

        await Should.ThrowAsync<AccessDeniedException>(() =>
            _knobs.SetEntryTtlAsync("acme-web", "h1", 7, TestContext.Current.CancellationToken));

        (await _store.GetMetadataAsync("acme-web", "h1", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    // Scenario 6: forgetting knobs applied in full.
    [Fact]
    public async Task FullMode_SweepThresholdAdjustment_IsApplied()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = "full";

        await _knobs.SetSweepThresholdAsync("acme-web", 0.1, TestContext.Current.CancellationToken);

        (await _knobs.GetSweepThresholdAsync("acme-web", TestContext.Current.CancellationToken)).ShouldBe(0.1);
    }

    [Fact]
    public async Task FullMode_EntryTtlAdjustment_IsApplied()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = "full";

        await _knobs.SetEntryTtlAsync("acme-web", "h1", 7, TestContext.Current.CancellationToken);

        var metadata = await _store.GetMetadataAsync("acme-web", "h1", TestContext.Current.CancellationToken);
        metadata!.TtlDays.ShouldBe(7);
    }

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, double> TtlByHash { get; } = [];

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();


        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default)
        {
            TtlByHash[hash] = ttlDays;
            return Task.CompletedTask;
        }

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(TtlByHash.TryGetValue(hash, out var ttl)
                ? new EntryMetadata(0.5, (int)ttl)
                : null);

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryEntry("h", "p.md", "project:acme", "v", 1));

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryStats(0, 0, []));



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

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult("{\"root\":\"\"}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("new-hash", path, context ?? "project:acme", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
    }
}
