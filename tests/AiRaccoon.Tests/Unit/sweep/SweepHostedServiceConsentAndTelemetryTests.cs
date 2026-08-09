using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.sweep;

/// <summary>
///     H6 (the reaper honours a project's access mode), H7 (a pass that deletes emits exactly one
///     span, a no-op pass emits none) and H8 (a pass with per-project failures does not report
///     result=success) — against a fast in-memory <see cref="FakeSweepStore" /> rather than
///     SweepHostedServiceTests' real SQLite bank, so many small scenarios stay cheap.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SweepHostedServiceConsentAndTelemetryTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static (FakeSweepStore Store, SweepHostedService Service) NewStack(IOperationTelemetry? telemetry = null)
    {
        var store = new FakeSweepStore();
        var time = new FakeTimeProvider(FixedNow);
        var service = new SweepHostedService(store, new SweepService(store, time), time,
            telemetry ?? TestTelemetry.None, NullLogger<SweepHostedService>.Instance);
        return (store, service);
    }

    // ---- H6: the reaper's consent ----

    [Fact]
    public async Task RunOnce_RoProject_EntrySurvives_FullProject_StillDeleted()
    {
        var (store, service) = NewStack();
        store.Projects.Clear();
        store.Projects.AddRange(["full-proj", "ro-proj"]);
        store.SeedSweepable("full-proj", "hash-full", FixedNow);
        store.SeedSweepable("ro-proj", "hash-ro", FixedNow);
        store.Settings[AccessModePolicy.ProjectSettingKey("full-proj")] = "full";
        store.Settings[AccessModePolicy.ProjectSettingKey("ro-proj")] = "ro";

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Deleted.ShouldContain(("full-proj", "hash-full"),
            "the fix must not make the reaper inert for a project that has consented");
        store.Deleted.ShouldNotContain(("ro-proj", "hash-ro"),
            "a project outside full mode has not consented to destructive background sweeps");
    }

    [Fact]
    public async Task RunOnce_ModeCannotBeResolved_DoesNotThrow_OtherProjectsStillSwept()
    {
        var (store, service) = NewStack();
        store.Projects.Clear();
        store.Projects.AddRange(["broken-proj", "full-proj"]);
        store.SeedSweepable("broken-proj", "hash-broken", FixedNow);
        store.SeedSweepable("full-proj", "hash-full", FixedNow);
        store.Settings[AccessModePolicy.ProjectSettingKey("full-proj")] = "full";
        store.SettingErrors[AccessModePolicy.ProjectSettingKey("broken-proj")] =
            new InvalidOperationException("settings unavailable");

        await Should.NotThrowAsync(() => service.RunOnceAsync(TestContext.Current.CancellationToken));

        store.Deleted.ShouldContain(("full-proj", "hash-full"));
        store.Deleted.ShouldNotContain(("broken-proj", "hash-broken"));
    }

    // ---- H7: span emission ----

    [Fact]
    public async Task RunOnce_DeletesAnEntry_EmitsExactlyOneSpan_WithADeletedCountAboveZero()
    {
        using var probe = new BackgroundTelemetryProbe(SweepHostedService.OperationName);
        var (store, service) = NewStack(probe.Telemetry);
        store.Projects.Clear();
        store.Projects.Add("acme");
        store.SeedSweepable("acme", "hash1", FixedNow);
        store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full";

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        var span = probe.Spans.ShouldHaveSingleItem();
        var deleted = span.GetTagItem("deleted");
        deleted.ShouldNotBeNull();
        Convert.ToInt32(deleted).ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task RunOnce_DeletesNothing_EmitsNoSpan_ButStillRecordsTheCounterAndDuration()
    {
        using var probe = new BackgroundTelemetryProbe(SweepHostedService.OperationName);
        var (store, service) = NewStack(probe.Telemetry);
        store.Projects.Clear();
        store.Projects.Add("acme");
        store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full";
        // No sweepable entry seeded: the pass has nothing to delete.

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        probe.Spans.ShouldBeEmpty("an empty pass must stay quiet on purpose (WP13 span-volume fix)");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
        probe.Durations.ShouldHaveSingleItem();
    }

    // ---- H8: a pass with failures does not report result=success ----

    [Fact]
    public async Task RunOnce_AllProjectsThrow_ResultIsNotSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(SweepHostedService.OperationName);
        var (store, service) = NewStack(probe.Telemetry);
        store.Projects.Clear();
        store.Projects.AddRange(["p1", "p2", "p3"]);
        foreach (var project in store.Projects)
        {
            store.Settings[AccessModePolicy.ProjectSettingKey(project)] = "full";
            store.FailingProjects.Add(project);
        }

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
    }

    [Fact]
    public async Task RunOnce_OneOfThreeThrows_OthersStillProcessed_AndTheFailureIsVisible()
    {
        using var probe = new BackgroundTelemetryProbe(SweepHostedService.OperationName);
        var (store, service) = NewStack(probe.Telemetry);
        store.Projects.Clear();
        store.Projects.AddRange(["p1", "p2", "p3"]);
        foreach (var project in store.Projects)
        {
            store.Settings[AccessModePolicy.ProjectSettingKey(project)] = "full";
        }

        store.SeedSweepable("p2", "hash-p2", FixedNow);
        store.SeedSweepable("p3", "hash-p3", FixedNow);
        store.FailingProjects.Add("p1");

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        store.Deleted.ShouldContain(("p2", "hash-p2"), "p1's failure must not abort p2");
        store.Deleted.ShouldContain(("p3", "hash-p3"), "p1's failure must not abort p3");
        var span = probe.Spans.ShouldHaveSingleItem();
        span.GetTagItem("failures").ShouldNotBeNull();
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
    }

    [Fact]
    public async Task RunOnce_CleanPass_StillRecordsSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(SweepHostedService.OperationName);
        var (store, service) = NewStack(probe.Telemetry);
        store.Projects.Clear();
        store.Projects.Add("acme");
        store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full";

        await service.RunOnceAsync(TestContext.Current.CancellationToken);

        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }
}

/// <summary>Minimal in-memory <see cref="IMemoryStore" />: multi-project settings/entries/metadata,
/// with per-project fault injection for the reaper's per-project try/catch.</summary>
internal sealed class FakeSweepStore : IMemoryStore
{
    public List<string> Projects { get; } = ["acme"];

    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Exception> SettingErrors { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<MemoryEntry>> Entries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EntryMetadata> MetadataByHash { get; } = new(StringComparer.Ordinal);

    public HashSet<(string ProjectId, string Hash)> Deleted { get; } = [];

    /// <summary>Project ids whose sweep must throw (per-project fault injection).</summary>
    public HashSet<string> FailingProjects { get; } = new(StringComparer.Ordinal);

    /// <summary>Seeds one entry old and rated low enough to be swept: ttlDays=1, rating=0.1, aged 10 years.</summary>
    public void SeedSweepable(string projectId, string hash, DateTimeOffset now)
    {
        var context = ContextNaming.ProjectContext(projectId);
        var createdAt = now.AddDays(-3650).ToUnixTimeSeconds();
        Entries.TryAdd(context, []);
        Entries[context].Add(new MemoryEntry(hash, $"{hash}.md", context, "value", createdAt));
        MetadataByHash[hash] = new EntryMetadata(0.1, 1);
    }

    public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(Projects);

    public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        if (FailingProjects.Contains(projectId))
        {
            throw new InvalidOperationException($"sweep-fault:{projectId}");
        }

        return Task.FromResult<IReadOnlyList<MemoryEntry>>(Entries.GetValueOrDefault(context) ?? []);
    }

    public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MetadataByHash.GetValueOrDefault(hash));

    public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        Deleted.Add((projectId, hash));
        foreach (var list in Entries.Values)
        {
            list.RemoveAll(e => e.Hash == hash);
        }

        return Task.FromResult(true);
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        SettingErrors.TryGetValue(key, out var error)
            ? throw error
            : Task.FromResult(Settings.GetValueOrDefault(key));

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(Settings
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value));

    public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Settings.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
        string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
