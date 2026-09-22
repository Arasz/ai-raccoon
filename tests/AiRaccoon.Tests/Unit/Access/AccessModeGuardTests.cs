using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using AiRaccoon.Tests.Unit.Projects;
using Xunit;

namespace AiRaccoon.Tests.Unit.Access;

/// <summary>
///     Guard resolution/enforcement and the forgetting-knob gate (FR-NM-2 scenarios 5-6; see docs/work/features-native-memory/native-memory.feature): knob
///     adjustments are destructive, denied in rw and applied in full.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
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
    public async Task Resolve_UnparseablePerProjectValue_FallsThroughToGlobal()
    {
        _store.Settings[AccessModePolicy.GlobalSettingKey] = "ro";
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "garbage";

        (await _guard.ResolveAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(AccessMode.Ro);
    }

    /// <summary>
    ///     Characterisation test: today's exact-key lookup cannot confuse projects by construction. It only
    ///     guards a naive prefix implementation that picks any key under the prefix instead of the exact one.
    /// </summary>
    [Fact]
    public async Task Resolve_DoesNotConfuseAnotherProjectsKey()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("other")] = "ro";

        (await _guard.ResolveAsync("acme", TestContext.Current.CancellationToken)).ShouldBe(AccessMode.Rw);
    }

    /// <summary>
    ///     d-426 SHOULD-1 + d-425 SHOULD-3: CLI key writes fold at construction while the MCP choke
    ///     folds only once migrated — a pre-P4 (raw-spelling) per-project key must still be honored
    ///     pre-migration, or the mode silently falls back to global (fail-OPEN). The guard tries the
    ///     stored spelling too, so the legacy key fails CLOSED.
    ///     Ledger — legacy-raw-key-miss : --filter Resolve_LegacyRawSpellingKey_IsEnforced_FailsClosed : raw loser key + default global.
    /// </summary>
    [Fact]
    public async Task Resolve_LegacyRawSpellingKey_IsEnforced_FailsClosed()
    {
        _store.Settings["access.mode.project:job-search-ai-assistant"] = "ro";

        (await _guard.ResolveAsync("job-search-ai-assistant", TestContext.Current.CancellationToken))
            .ShouldBe(AccessMode.Ro);
    }

    /// <summary>
    ///     ADR-0099: with the empty default the folded key and the raw key are the same string —
    ///     there are no longer two spellings to arbitrate. The last write wins like any single key.
    ///     Ledger — raw-overrides-canonical : --filter Resolve_BothSpellingsPresent_CanonicalWins : folded rw + raw ro.
    /// </summary>
    [Fact]
    public async Task Resolve_BothSpellingsPresent_CanonicalWins()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("job-search-ai-assistant")] = "rw";
        _store.Settings["access.mode.project:job-search-ai-assistant"] = "ro";

        (await _guard.ResolveAsync("job-search-ai-assistant", TestContext.Current.CancellationToken))
            .ShouldBe(AccessMode.Ro, "folded and raw are one key now — last write wins");
    }

    [Fact]
    public async Task EnsureAsync_ForWrite_ReadsSettingsStoreOnce()
    {
        var counting = new CountingStore();

        await new MemoryAccessGuard(counting).EnsureAsync("acme", AccessRequirement.Write, "memory_write",
            TestContext.Current.CancellationToken);

        counting.SettingsReadCount.ShouldBe(1);
    }

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

        (await _knobs.GetSweepThresholdAsync(TestContext.Current.CancellationToken)).ShouldBe(0.3);
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

        (await _knobs.GetSweepThresholdAsync(TestContext.Current.CancellationToken)).ShouldBe(0.1);
    }

    [Fact]
    public async Task FullMode_EntryTtlAdjustment_IsApplied()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = "full";

        await _knobs.SetEntryTtlAsync("acme-web", "h1", 7, TestContext.Current.CancellationToken);

        var metadata = await _store.GetMetadataAsync("acme-web", "h1", TestContext.Current.CancellationToken);
        metadata!.TtlDays.ShouldBe(7);
    }

    /// <summary>Permits every guarded call, so a denial in a test comes from the guard and not the store.</summary>
    private sealed class FakeStore : FakeMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int?> TtlByHash { get; } = [];

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(Settings
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

        public override Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
            CancellationToken cancellationToken = default)
        {
            TtlByHash[hash] = ttlDays;
            return Task.FromResult(true);
        }

        public override Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(TtlByHash.TryGetValue(hash, out var ttl)
                ? new EntryMetadata(0.5, ttl)
                : null);

        public override Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("h", "p.md", "project:acme", "v", 1));

        public override Task<SearchResults> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults([], SearchTimings.Empty));

        public override Task<bool> DeleteAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public override Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public override Task<MemoryStats> GetStatsAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryStats(0, 0, []));

        public override Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntryResult(new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1), true));

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
            Task.FromResult(new MemoryEntryResult(new MemoryEntry("new-hash", path, context ?? "project:acme", content, 1), true));

        public override Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
    }

    /// <summary>Counts every settings read, regardless of shape, so a batched read is distinguishable from two single-key reads.</summary>
    private sealed class CountingStore : FakeMemoryStore
    {
        public int SettingsReadCount { get; private set; }

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            SettingsReadCount++;
            return Task.FromResult<string?>(null);
        }

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default)
        {
            SettingsReadCount++;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }
}
