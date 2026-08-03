using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     End-to-end store tests against the managed memory.db implementation (no sqliteai native
///     extensions, no provisioning): the "done means proven" gate for the P1 store. Embedding and
///     dedup semantics belong to P4/P3 respectively and are not exercised here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteMemoryStore _store;
    private readonly WorkspaceService _workspaces;

    public SqliteMemoryStoreIntegrationTests()
    {
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadExtensions: _ => { });
        _store = new SqliteMemoryStore(factory, new FakeTimeProvider(FixedNow), new TokenizerChunker(),
            new AiRaccoon.Infrastructure.Embedding.EmbeddingService());
        _workspaces = new WorkspaceService(_store, new SqliteWorkspaceStore(factory), new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task Write_StoresEntryInProjectContext()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "SQLite memory stores project knowledge"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("project:acme");
        entry.Hash.ShouldNotBeNullOrWhiteSpace();
        entry.Value.ShouldBe("SQLite memory stores project knowledge");
    }

    [Fact]
    public async Task Write_WithWorkspaceId_LandsInWorkspaceContext()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "draft finding", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("workspace:ws-1");
    }

    [Fact]
    public async Task ShareAsync_PromotesIntoSharedContext_AndKeepsTheSource()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "cross project convention"),
            TestContext.Current.CancellationToken);

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        shared.Context.ShouldBe(ContextNaming.SharedContext);
        var sharedEntries = await _store.ListContextAsync("acme", ContextNaming.SharedContext,
            TestContext.Current.CancellationToken);
        sharedEntries.ShouldContain(e => e.Value == "cross project convention");
        var projectEntries = await _store.ListContextAsync("acme", "project:acme",
            TestContext.Current.CancellationToken);
        projectEntries.ShouldContain(e => e.Value == "cross project convention");
    }

    [Fact]
    public async Task ShareAsync_Twice_IsIdempotent()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "share me once"), TestContext.Current.CancellationToken);

        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        var sharedEntries = await _store.ListContextAsync("acme", ContextNaming.SharedContext,
            TestContext.Current.CancellationToken);
        sharedEntries.Count(e => e.Value == "share me once").ShouldBe(1);
    }

    [Fact]
    public async Task DeleteAsync_RemovesEntry()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "to be deleted"), TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        deleted.ShouldBeTrue();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Stats_ReportsCommittedContexts()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "context a note"), TestContext.Current.CancellationToken);

        var stats = await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken);

        stats.EntryCount.ShouldBeGreaterThanOrEqualTo(1);
        stats.Contexts.ShouldContain("project:acme");
    }

    [Fact]
    public async Task ListContextAsync_ReturnsEntriesForTheContext()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "listed entry"), TestContext.Current.CancellationToken);

        var entries = await _store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken);

        entries.ShouldContain(e => e.Value == "listed entry");
    }

    [Fact]
    public async Task Search_ReturnsStoredEntry_ByKeyword_WithoutEmbeddings()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "semantic searchable fact"), TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "semantic searchable"),
            TestContext.Current.CancellationToken);

        results.ShouldContain(r => r.Hash == entry.Hash);
    }

    [Fact]
    public async Task WorkspaceConsolidate_PromotesKeptHash_ThenEmptiesWorkspace()
    {
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "workspace durable fact", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var result = await _workspaces.ConsolidateAsync("acme", "ws-1", ["all"],
            TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(1);
        var projectEntries = await _store.ListContextAsync("acme", "project:acme",
            TestContext.Current.CancellationToken);
        projectEntries.ShouldContain(e => e.Value == "workspace durable fact");
        var workspaceEntries = await _store.ListContextAsync("acme", "workspace:ws-1",
            TestContext.Current.CancellationToken);
        workspaceEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Settings_UpsertAndRead_RoundTrip()
    {
        await _store.SetSettingAsync("test.setting", "value-1", TestContext.Current.CancellationToken);
        (await _store.GetSettingAsync("test.setting", TestContext.Current.CancellationToken))
            .ShouldBe("value-1");
    }

    [Fact]
    public async Task Settings_Get_MissingKey_ReturnsNull()
    {
        (await _store.GetSettingAsync("test.missing", TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task SetEntryTtl_UpdatesTheRowsTtlOverride()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "forgettable note"), TestContext.Current.CancellationToken);

        await _store.SetEntryTtlAsync("acme", entry.Hash, 7, TestContext.Current.CancellationToken);

        var metadata = await _store.GetMetadataAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        metadata!.TtlDays.ShouldBe(7);
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
