using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly SqlitePromotionQueueStore _queue;
    private readonly SqliteMemoryStore _store;
    private readonly WorkspaceService _workspaces;

    public SqliteMemoryStoreIntegrationTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            new EmbeddingService());
        _workspaces = new WorkspaceService(_store, new SqliteWorkspaceStore(_factory), new FakeTimeProvider(FixedNow));
        _queue = new SqlitePromotionQueueStore(_factory, new FakeTimeProvider(FixedNow));
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
        var ws = await _workspaces.BeginAsync("acme", cancellationToken: TestContext.Current.CancellationToken);

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

        shared.Entry.Context.ShouldBe(ContextNaming.SharedContext);
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
    public async Task ShareAsync_CarriesSourceFileAndSection_IntoTheSharedRow()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "cross project convention", SourceFile: "docs/guide.md",
                Section: "decision"),
            TestContext.Current.CancellationToken);

        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "cross project convention", Scope: SearchScope.Shared),
            TestContext.Current.CancellationToken);
        results.ShouldContain(r => r.SourceFile == "docs/guide.md");

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var section = await connection.QueryFirstOrDefaultAsync<string?>(
            "SELECT section FROM entries WHERE scope = 'shared' AND value = @value",
            new { value = "cross project convention" });
        section.ShouldBe("decision");
    }

    [Fact]
    public async Task ExtractCandidates_ExcludesConfiguredSourceFilePrefixes()
    {
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "hermes session scratch", SourceFile: "hermes/session-1"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "docs fact", SourceFile: "docs/a.md"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "organic fact"),
            TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE entries SET embed_state = 'embedded'");
        }

        var before = await _store.ExtractCandidatesAsync("acme", includeTtlRows: false,
            TestContext.Current.CancellationToken);
        before.Count.ShouldBe(3);

        await _store.SetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, "hermes/",
            TestContext.Current.CancellationToken);

        var after = await _store.ExtractCandidatesAsync("acme", includeTtlRows: false,
            TestContext.Current.CancellationToken);
        after.Count.ShouldBe(2);
        after.ShouldNotContain(c => c.SourceFile == "hermes/session-1");
        after.ShouldContain(c => c.SourceFile == "docs/a.md");
        after.ShouldContain(c => c.SourceFile == null);
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
        var ws = await _workspaces.BeginAsync("acme", cancellationToken: TestContext.Current.CancellationToken);

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

    /// <summary>Ingest is contained by the declared scope, so a test that ingests declares one.</summary>
    private Task ScopeDataRootAsync() =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task DeleteSourcePath_RemovesAllChunksOfTheFile_AndSearchStopsReturningIt()
    {
        var file = Path.Combine(_dataRoot, "notes.md");
        await File.WriteAllTextAsync(file, "magnetostrictive mirror content", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
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
        await ScopeDataRootAsync();
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
        var ws = await _workspaces.BeginAsync("acme", cancellationToken: TestContext.Current.CancellationToken);
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
        await ScopeDataRootAsync();
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
    public async Task DeleteSourcePath_LeavesManualRowsThatCiteThePathAlone()
    {
        var file = Path.Combine(_dataRoot, "cited.md");
        await File.WriteAllTextAsync(file, "magnetostrictive cited content", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);
        // A manual write stores path = <sha256(content)>.md and keeps the caller's sourceFile —
        // the digest's replace-by-path delete must not own it.
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "kaleidophrenic manual note", SourceFile: file),
            TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteSourcePathAsync("acme", file, TestContext.Current.CancellationToken);

        deleted.ShouldBeGreaterThan(0);
        (await _store.SearchAsync(new SearchQuery("acme", "magnetostrictive"),
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty("the file's own mirror chunks must be removed");
        (await _store.SearchAsync(new SearchQuery("acme", "kaleidophrenic"),
                TestContext.Current.CancellationToken))
            .ShouldNotBeEmpty("a manual row citing the path as sourceFile must survive");
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReplaceFile_KeepsPromotionCandidateBackedOnlyByManualRow()
    {
        var file = Path.Combine(_dataRoot, "cited-queue.md");
        await File.WriteAllTextAsync(file, "oscillatory queued source", TestContext.Current.CancellationToken);
        await ScopeDataRootAsync();
        await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        // The manual row cites the watched file but is not owned by it: path = <sha256>.md.
        // Its promotion candidate is backed by that row alone.
        var manual = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "thixotropic queued manual", SourceFile: file),
            TestContext.Current.CancellationToken);
        await _queue.UpsertAsync("acme",
            [new QueueCandidate(manual.Hash, $"{manual.Hash}.md", manual.Value, file, 1.0, [])],
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(file, "oscillatory queued revised", TestContext.Current.CancellationToken);
        await _store.ReplaceFileAsync("acme", file, "revised-hash", TestContext.Current.CancellationToken);

        (await _queue.ListAsync("acme", TestContext.Current.CancellationToken))
            .ShouldContain(r => r.Hash == manual.Hash,
                "a candidate backed only by a manual row citing the path must survive the digest replace");
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

    [Fact]
    public async Task WriteSearchRoundtrip_IncludesSourceIdentity()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "canonical source identity fact", SourceFile: "docs/architecture.md",
                Section: "decisions"),
            TestContext.Current.CancellationToken);

        // WP7: every entry written through the store must have a valid source_id FK.
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sourceId = await connection.ExecuteScalarAsync<long?>(
            "SELECT source_id FROM entries WHERE hash = @hash", new { hash = entry.Hash });
        sourceId.ShouldNotBeNull("WriteAsync must set source_id on the entry");
        sourceId.Value.ShouldBeGreaterThan(0, "source_id must reference a real memory_source row");

        var sourceType = await connection.ExecuteScalarAsync<string>(
            "SELECT source_type FROM memory_source WHERE id = @id", new { id = sourceId.Value });
        sourceType.ShouldBe("file", "a SourceFile write must resolve to source_type='file'");

        // Search round-trip: the result must still carry SourceFile for backwards compatibility.
        var results = await _store.SearchAsync(
            new SearchQuery("acme", "canonical source identity"), TestContext.Current.CancellationToken);
        results.ShouldContain(r => r.SourceFile == "docs/architecture.md");
    }

    [Fact]
    public async Task ShareExtract_PreservesSourceIdentity()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "shareable source identity", SourceFile: "docs/guide.md"),
            TestContext.Current.CancellationToken);

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        // The shared row must also carry a valid source_id.
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sharedSourceId = await connection.ExecuteScalarAsync<long?>(
            "SELECT source_id FROM entries WHERE scope = 'shared' AND value = @value",
            new { value = "shareable source identity" });
        sharedSourceId.ShouldNotBeNull("the shared row must have source_id populated");
        sharedSourceId.Value.ShouldBeGreaterThan(0);

        // The shared entry's SourceFile is carried into search results.
        var results = await _store.SearchAsync(
            new SearchQuery("acme", "shareable source identity", Scope: SearchScope.Shared),
            TestContext.Current.CancellationToken);
        results.ShouldContain(r => r.SourceFile == "docs/guide.md");
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot();
}
