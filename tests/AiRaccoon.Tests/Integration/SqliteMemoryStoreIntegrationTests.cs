using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Dapper;
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
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;
    private readonly WorkspaceService _workspaces;

    public SqliteMemoryStoreIntegrationTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new TokenizerChunker(),
            new EmbeddingService());
        _workspaces = new WorkspaceService(_store, new SqliteWorkspaceStore(_factory), new FakeTimeProvider(FixedNow));
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
        var ws = await _workspaces.BeginAsync("acme", TestContext.Current.CancellationToken);

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "draft finding", WorkspaceId: ws.Id),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe($"workspace:{ws.Id}");
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
        var ws = await _workspaces.BeginAsync("acme", TestContext.Current.CancellationToken);

        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "workspace durable fact", WorkspaceId: ws.Id),
            TestContext.Current.CancellationToken);

        var result = await _workspaces.ConsolidateAsync("acme", ws.Id, ["all"],
            TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(1);
        var projectEntries = await _store.ListContextAsync("acme", "project:acme",
            TestContext.Current.CancellationToken);
        projectEntries.ShouldContain(e => e.Value == "workspace durable fact");
        var workspaceEntries = await _store.ListContextAsync("acme", $"workspace:{ws.Id}",
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
    public async Task Settings_Get_MissingKey_ReturnsNull() =>
        (await _store.GetSettingAsync("test.missing", TestContext.Current.CancellationToken))
        .ShouldBeNull();

    [Fact]
    public async Task SetEntryTtl_UpdatesTheRowsTtlOverride()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "forgettable note"), TestContext.Current.CancellationToken);

        await _store.SetEntryTtlAsync("acme", entry.Hash, 7, TestContext.Current.CancellationToken);

        var metadata = await _store.GetMetadataAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        metadata!.TtlDays.ShouldBe(7);
    }

    [Fact]
    public async Task DeleteSourcePath_RemovesAllChunksOfTheFile_AndSearchStopsReturningIt()
    {
        var file = Path.Combine(_dataRoot, "notes.md");
        await File.WriteAllTextAsync(file, "magnetostrictive mirror content", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        (await _store.SearchAsync(new SearchQuery("acme", "magnetostrictive"),
                TestContext.Current.CancellationToken))
            .ShouldNotBeEmpty();

        var deleted = await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        deleted.ShouldBeGreaterThan(0);
        (await _store.SearchAsync(new SearchQuery("acme", "magnetostrictive"),
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteSourcePath_RemovesOnlyThatProjectsRows_ForTheSamePath()
    {
        var file = Path.Combine(_dataRoot, "shared-source.md");
        await File.WriteAllTextAsync(file, "magnetostrictive cross project content", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("beta", file, null, TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        deleted.ShouldBeGreaterThan(0);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(0);
        (await _store.GetStatsAsync("beta", TestContext.Current.CancellationToken)).EntryCount.ShouldBeGreaterThan(0);
        (await _store.SearchAsync(new SearchQuery("beta", "magnetostrictive"),
                TestContext.Current.CancellationToken))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task DeleteSourcePath_LeavesWorkspaceScratchRowsForThePathAlone()
    {
        var ws = await _workspaces.BeginAsync("acme", TestContext.Current.CancellationToken);
        var file = Path.Combine(_dataRoot, "scratch.md");
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "magnetostrictive workspace scratch",
                WorkspaceId: ws.Id, SourceFile: file),
            TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        deleted.ShouldBe(0);
        (await _store.ListContextAsync("acme", $"workspace:{ws.Id}", TestContext.Current.CancellationToken))
            .ShouldNotBeEmpty();
    }

    [Fact]
    public async Task DeleteSourcePath_ClearsWatchFingerprint_ButKeepsTheWatchRegistration()
    {
        var file = Path.Combine(_dataRoot, "watched.md");
        await File.WriteAllTextAsync(file, "magnetostrictive watched content", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES (@projectId, @path, @now, @now)",
                new { projectId = "acme", path = file, now = 1L });
            await connection.ExecuteAsync(
                "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES (@projectId, @path, @hash, @now)",
                new { projectId = "acme", path = file, hash = "abc123", now = 1L });
        }

        var deleted = await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        deleted.ShouldBeGreaterThan(0);
        await using var verify = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await verify.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM watch_files WHERE project_id = @projectId AND path = @path",
                new { projectId = "acme", path = file }))
            .ShouldBe(0);
        (await verify.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM watches WHERE project_id = @projectId AND path = @path",
                new { projectId = "acme", path = file }))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Schema_CreatesWatchTables_OnFreshBank()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var tables = (await connection.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('watches', 'watch_files')"))
            .ToList();

        tables.ShouldContain("watches");
        tables.ShouldContain("watch_files");
    }

    [Fact]
    public async Task Schema_ExistingBankWithoutWatchTables_GainsThemOnReopen_WithoutDisturbingEntries()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "pre feature entry"),
            TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            // Simulate a bank created before the watch feature: drop the watch tables only.
            await connection.ExecuteAsync("DROP TABLE IF EXISTS watches; DROP TABLE IF EXISTS watch_files;");
        }

        await using var reopened = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var tables = (await reopened.QueryAsync<string>(
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('watches', 'watch_files')"))
            .ToList();
        tables.ShouldContain("watches");
        tables.ShouldContain("watch_files");
        (await reopened.ExecuteScalarAsync<long>("SELECT count(*) FROM entries_fts"))
            .ShouldBe(await reopened.ExecuteScalarAsync<long>("SELECT count(*) FROM entries"));
        (await _store.SearchAsync(new SearchQuery("acme", "pre feature"),
                TestContext.Current.CancellationToken))
            .ShouldNotBeEmpty();
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("ai-raccoon-tests");
}
