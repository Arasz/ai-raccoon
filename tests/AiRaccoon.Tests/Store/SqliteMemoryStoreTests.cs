using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMemoryStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public SqliteMemoryStoreTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" });
        _store = new SqliteMemoryStore(_factory, new FakeTimeProvider(FixedNow), new StubChunker(),
            new AiRaccoon.Infrastructure.Embedding.EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task EnsureWorkspaceAsync(string workspaceId, string projectId = "acme")
    {
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync(projectId, workspaceId, FixedNow, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Write_CreatesRowInProjectScope_WithPendingEmbedState_AndOnRowDefaults()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "SQLite memory stores project knowledge"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("project:acme");
        entry.Hash.ShouldNotBeNullOrWhiteSpace();
        entry.Value.ShouldBe("SQLite memory stores project knowledge");
        entry.CreatedAt.ShouldBe(FixedNow.ToUnixTimeSeconds());

        var row = await ReadRowAsync(entry.Hash);
        row.ShouldNotBeNull();
        row.Scope.ShouldBe("project");
        row.ProjectId.ShouldBe("acme");
        row.EmbedState.ShouldBe("pending");
        row.Rating.ShouldBe(RatingPolicy.DefaultBaseScore);
        row.AccessCount.ShouldBe(0);
        row.Path.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Write_WithWorkspace_LandsInWorkspaceScope()
    {
        await EnsureWorkspaceAsync("ws-1");

        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "draft finding", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("workspace:ws-1");

        var row = await ReadRowAsync(entry.Hash);
        row.ShouldNotBeNull();
        row.WorkspaceId.ShouldBe("ws-1");
        row.Scope.ShouldBeNull();
    }

    [Fact]
    public async Task Write_WithExplicitContext_UsesCustomScope_AndStoresAgentId()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "docs only fact", "docs:api", "agent-1"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("docs:api");

        var row = await ReadRowAsync(entry.Hash);
        row.ShouldNotBeNull();
        row.Scope.ShouldBe("custom");
        row.ContextLabel.ShouldBe("docs:api");
        row.AgentId.ShouldBe("agent-1");
    }

    [Fact]
    public async Task Search_FindsKeywordMatch_AndCarriesTheContractFields()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "SQLite memory stores project knowledge"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "knowledge"),
            TestContext.Current.CancellationToken);

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Path.ShouldBe(entry.Path);
        hit.Snippet.ShouldNotBeNullOrWhiteSpace();
        // P6 contract: ranking is normalized into 0..1 with the top result at 1.0.
        hit.Ranking.ShouldBeInRange(0.0, 1.0);
        hit.Ranking.ShouldBe(1.0);
    }

    [Fact]
    public async Task Search_WithoutEmbeddingEngine_KeywordOnlyQuery_ReturnsKeywordResultsAboveMinScore()
    {
        // FR-NM-4 s2: no engine configured -> the vec modality is absent. The keyword query
        // must still return results above the minimum score without crashing.
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "the only exact keyword phrase present is ziggurat"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "ziggurat", minScore: 0.7),
            TestContext.Current.CancellationToken);

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.Ranking.ShouldBe(1.0);
    }

    [Fact]
    public async Task Search_ProjectScope_ExcludesOtherProjectsAndCustomContexts()
    {
        var acmeEntry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "acme project fact"), TestContext.Current.CancellationToken);
        var otherEntry = await _store.WriteAsync(
            new MemoryWriteRequest("other", "other project fact"), TestContext.Current.CancellationToken);
        var customEntry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "custom context fact", "docs:api"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "fact", SearchScope.Project),
            TestContext.Current.CancellationToken);

        results.Select(r => r.Hash).ShouldContain(acmeEntry.Hash);
        results.ShouldNotContain(r => r.Hash == otherEntry.Hash);
        results.ShouldNotContain(r => r.Hash == customEntry.Hash);
    }

    [Fact]
    public async Task Search_BumpsAccessCountAndRating_OnTheReturnedRow()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "frequently retrieved fact"),
            TestContext.Current.CancellationToken);

        await _store.SearchAsync(new SearchQuery("acme", "retrieved"), TestContext.Current.CancellationToken);

        var row = await ReadRowAsync(entry.Hash);
        row.ShouldNotBeNull();
        row.AccessCount.ShouldBe(1);
        row.Rating.ShouldBeGreaterThan(RatingPolicy.DefaultBaseScore);
        row.LastAccessedAt.ShouldBe(FixedNow.ToUnixTimeSeconds());

        var metadata = await _store.GetMetadataAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        metadata.ShouldNotBeNull();
        metadata!.Rating.ShouldBe(row.Rating);
    }

    [Fact]
    public async Task Share_CopiesRowIntoSharedScope_PreservingPath()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "cross project convention"),
            TestContext.Current.CancellationToken);

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        shared.Context.ShouldBe(ContextNaming.SharedContext);
        shared.Path.ShouldStartWith("shared/");
        (await _store.ListContextAsync("acme", ContextNaming.SharedContext, TestContext.Current.CancellationToken))
            .ShouldContain(e => e.Value == "cross project convention");
        (await _store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken))
            .ShouldContain(e => e.Value == "cross project convention");
    }

    [Fact]
    public async Task Share_Twice_IsIdempotent()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "share me once"), TestContext.Current.CancellationToken);

        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        (await _store.ListContextAsync("acme", ContextNaming.SharedContext, TestContext.Current.CancellationToken))
            .Count(e => e.Value == "share me once").ShouldBe(1);
    }

    [Fact]
    public async Task Delete_RemovesTheRows_AndTheFtsIndexEntry()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "to be deleted"), TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        deleted.ShouldBeTrue();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(0);
        (await _store.SearchAsync(
                new SearchQuery("acme", "deleted", minScore: 0),
                TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_ForAnotherProject_DoesNotRemoveTheRow()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "belongs to acme"), TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteAsync("other", entry.Hash, TestContext.Current.CancellationToken);

        deleted.ShouldBeFalse();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteContext_WorkspaceContext_RemovesWorkspaceRowsOnly()
    {
        await EnsureWorkspaceAsync("ws-1");
        await _store.WriteAsync(new MemoryWriteRequest("acme", "workspace draft", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "committed fact"), TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteContextAsync("acme", "workspace:ws-1", TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);
        (await _store.ListContextAsync("acme", "workspace:ws-1", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task Stats_CountsCommittedEntries_AndPendingFromEmbedState()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "committed fact"), TestContext.Current.CancellationToken);
        await EnsureWorkspaceAsync("ws-1");
        await _store.WriteAsync(new MemoryWriteRequest("acme", "workspace draft", workspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var stats = await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken);

        stats.EntryCount.ShouldBe(1);
        stats.PendingCount.ShouldBe(2);
        stats.Contexts.ShouldContain("project:acme");
    }

    [Fact]
    public async Task AddContent_IsIdempotent_ByPathInBucket()
    {
        var first = await _store.AddContentAsync("acme", "docs/note.md", "note content", null,
            TestContext.Current.CancellationToken);
        var second = await _store.AddContentAsync("acme", "docs/note.md", "note content", null,
            TestContext.Current.CancellationToken);

        second.Hash.ShouldBe(first.Hash);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task Write_SameContentTwice_ReturnsTheExistingEntry_AndStatsReportOneEntry()
    {
        var first = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "identical content"), TestContext.Current.CancellationToken);
        var second = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "identical content"), TestContext.Current.CancellationToken);

        second.Hash.ShouldBe(first.Hash);
        second.Path.ShouldBe(first.Path);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task Share_CreatesARealSharedRow_WithDistinctPathScopedHash()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "cross project fact"), TestContext.Current.CancellationToken);

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        shared.Context.ShouldBe(ContextNaming.SharedContext);
        shared.Path.ShouldBe($"shared/{entry.Path}");
        shared.Value.ShouldBe(entry.Value);
        shared.Hash.ShouldNotBe(entry.Hash);
        shared.Hash.ShouldBe(ContentHash.Of($"shared/{entry.Path}", entry.Value));
        // The source row stays in the project scope: both rows exist after sharing.
        (await _store.ListContextAsync("acme", ContextNaming.SharedContext, TestContext.Current.CancellationToken))
            .Count(e => e.Value == "cross project fact").ShouldBe(1);
        (await _store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken))
            .Count(e => e.Value == "cross project fact").ShouldBe(1);
    }

    [Fact]
    public async Task AddContent_ConsolidationPromotion_PreservesTheLogicalPath_InTheCommittedRow()
    {
        await EnsureWorkspaceAsync("ws-1");
        // Consolidation (WorkspaceService) promotes via add_content with the workspace entry's
        // path: the committed row must keep that logical path and its path-scoped hash.
        await _store.AddContentAsync("acme", "docs/note.md", "workspace draft", "workspace:ws-1",
            TestContext.Current.CancellationToken);

        var committed = await _store.AddContentAsync("acme", "docs/note.md", "workspace draft",
            ContextNaming.ProjectContext("acme"), TestContext.Current.CancellationToken);

        committed.Context.ShouldBe("project:acme");
        committed.Path.ShouldBe("docs/note.md");
        committed.Hash.ShouldBe(ContentHash.Of("docs/note.md", "workspace draft"));
    }

    [Fact]
    public async Task ListFiles_ReturnsJsonTree_FromEntryPaths()
    {
        await _store.AddContentAsync("acme", "docs/guide.md", "guide", null, TestContext.Current.CancellationToken);
        await _store.AddContentAsync("acme", "notes.md", "notes", null, TestContext.Current.CancellationToken);

        var tree = await _store.ListFilesAsync("acme", TestContext.Current.CancellationToken);

        tree.ShouldContain("docs");
        tree.ShouldContain("guide.md");
        tree.ShouldContain("notes.md");
    }

    [Fact]
    public async Task EmbedPending_ReturnsZeroProcessed_AndPendingCount()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "pending note"), TestContext.Current.CancellationToken);

        var result = await _store.EmbedPendingAsync("acme", null, TestContext.Current.CancellationToken);

        result.Processed.ShouldBe(0); // no engine configured → nothing can be embedded (FR-NM-3 s4)
        result.Pending.ShouldBe(1);
    }

    [Fact]
    public async Task ConfigureEmbedding_StoresProviderAndModel_InSettings()
    {
        var config = await _store.ConfigureEmbeddingAsync("acme", "openai", "nomic-embed-text", null, null,
            TestContext.Current.CancellationToken);

        config.Provider.ShouldBe("openai");
        config.Model.ShouldBe("nomic-embed-text");

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var provider = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.provider'");
        var model = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.model'");
        var engine = await connection.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.engine'");
        provider.ShouldBe("openai");
        model.ShouldBe("nomic-embed-text");
        engine.ShouldBe("openai:nomic-embed-text@https://api.openai.com/v1");
    }

    [Fact]
    public async Task IngestFile_ChunksContent_ThroughTheChunker_AndReturnsIndexedCount()
    {
        var file = Path.Combine(_dataRoot, "long-note.md");
        await File.WriteAllTextAsync(file, "first chunk\n\nsecond chunk\n\nthird chunk",
            TestContext.Current.CancellationToken);

        var indexed = await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken);

        indexed.ShouldBe(1);
        var entries = await _store.ListContextAsync("acme", "project:acme", TestContext.Current.CancellationToken);
        entries.Count.ShouldBe(3);
        entries.ShouldAllBe(e => e.Path == file);

        // Unchanged file is skipped on re-ingest.
        (await _store.IngestFileAsync("acme", file, null, TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task IngestDirectory_IndexesMarkdownFiles_AndSkipsUnchanged()
    {
        var dir = Path.Combine(_dataRoot, "docs");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "a.md"), "alpha content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "b.md"), "beta content", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.txt"), "plain text", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dir, "image.png"), "not text", TestContext.Current.CancellationToken);

        var first = await _store.IngestDirectoryAsync("acme", dir, null, TestContext.Current.CancellationToken);
        var second = await _store.IngestDirectoryAsync("acme", dir, null, TestContext.Current.CancellationToken);

        first.ShouldBe(3); // a.md + b.md + notes.txt
        second.ShouldBe(0);
    }

    [Fact]
    public void SearchContexts_AllScope_SpansSharedProjectAndNamedWorkspace()
    {
        var query = new SearchQuery("acme", "q", SearchScope.All, "ws-1");

        SearchContexts.For(query).ShouldBe(
        [
            ContextNaming.SharedContext, ContextNaming.ProjectContext("acme"), ContextNaming.WorkspaceContext("ws-1")
        ]);
    }

    [Fact]
    public void SearchContexts_AllScope_WithoutWorkspace_SpansSharedAndProject()
    {
        var query = new SearchQuery("acme", "q");

        SearchContexts.For(query).ShouldBe(
            [ContextNaming.SharedContext, ContextNaming.ProjectContext("acme")]);
    }

    [Fact]
    public void SearchContexts_ProjectScope_SpansProjectAndNamedWorkspace()
    {
        var query = new SearchQuery("acme", "q", SearchScope.Project, "ws-1");

        SearchContexts.For(query).ShouldBe(
            [ContextNaming.ProjectContext("acme"), ContextNaming.WorkspaceContext("ws-1")]);
    }

    [Fact]
    public void SearchContexts_ProjectScope_WithoutWorkspace_IsProjectOnly()
    {
        var query = new SearchQuery("acme", "q", SearchScope.Project);

        SearchContexts.For(query).ShouldBe([ContextNaming.ProjectContext("acme")]);
    }

    [Fact]
    public void SearchContexts_SharedScope_IsSharedOnly_EvenWhenWorkspaceNamed()
    {
        var query = new SearchQuery("acme", "q", SearchScope.Shared, "ws-1");

        SearchContexts.For(query).ShouldBe([ContextNaming.SharedContext]);
    }

    // The RRF rework (P6) fuses per-context batches by rank position, not best ranking.
    [Fact]
    public void Merge_RrfAcrossContextBatches_PromotesDualRetrievedDocs_AndNormalizesToMax()
    {
        var shared = new[] { Hit("h1", 1, "a.md"), Hit("h2", 2, "b.md") };
        var project = new[] { Hit("h2", 1, "b.md"), Hit("h3", 2, "c.md") };

        var merged = SearchResultMerger.Merge([shared, project], 10, rrfK: 60);

        // h1 = 1/61, h3 = 1/62, h2 = 1/61 + 1/62 -> h2 ranks first and normalizes to 1.0.
        merged.Select(r => r.Hash).ShouldBe(["h2", "h1", "h3"]);
        merged[0].Ranking.ShouldBe(1.0);
        merged[1].Ranking.ShouldBe(62.0 / 123, 1e-9);
    }

    [Fact]
    public void Merge_SingleContextBatch_KeepsItsOrderAndNormalizesTopToOne()
    {
        var results = new[] { Hit("h1", 1, "a.md"), Hit("h2", 2, "b.md"), Hit("h3", 3, "c.md") };

        var merged = SearchResultMerger.Merge([results], 10);

        merged.Select(r => r.Hash).ShouldBe(["h1", "h2", "h3"]);
        merged[0].Ranking.ShouldBe(1.0);
    }

    [Fact]
    public void Merge_AppliesMinScoreAfterNormalization()
    {
        var results = new[] { Hit("h1", 1, "a.md"), Hit("h2", 2, "b.md"), Hit("h3", 3, "c.md") };

        var merged = SearchResultMerger.Merge([results], 10, minScore: 0.9, rrfK: 10);

        // Single-list scores 11/11, 11/12, 11/13; only the top two clear 0.9.
        merged.Select(r => r.Hash).ShouldBe(["h1", "h2"]);
    }

    [Fact]
    public void Merge_SortsByFusedScoreDescending_AndLimits()
    {
        var results = new[]
        {
            new MemorySearchResult("h1", 1, 0.4, "a.md", "s"),
            new MemorySearchResult("h2", 2, 0.9, "b.md", "s"),
            new MemorySearchResult("h3", 3, 0.7, "c.md", "s")
        };

        var merged = SearchResultMerger.Merge([results], 2);

        // Rank order decides, not the interim score payload.
        merged.Select(r => r.Hash).ShouldBe(["h1", "h2"]);
    }

    [Fact]
    public void Merge_EmptyBatches_ReturnsEmpty()
    {
        SearchResultMerger.Merge([Array.Empty<MemorySearchResult>(), Array.Empty<MemorySearchResult>()], 10)
            .ShouldBeEmpty();
    }

    private static MemorySearchResult Hit(string hash, int seq, string path) =>
        new(hash, seq, 0, path, "s");

    private async Task<EntryRow?> ReadRowAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QueryFirstOrDefaultAsync<EntryRow>(
            """
            SELECT hash AS Hash, path AS Path, scope AS Scope, project_id AS ProjectId,
                   context_label AS ContextLabel, workspace_id AS WorkspaceId, agent_id AS AgentId,
                   created_at AS CreatedAt, access_count AS AccessCount, last_accessed_at AS LastAccessedAt,
                   rating AS Rating, ttl_days AS TtlDays, embed_state AS EmbedState
            FROM entries
            WHERE hash = @hash
            ORDER BY id
            LIMIT 1
            """,
            new { hash });
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class EntryRow
    {
        public string Hash { get; set; } = "";

        public string Path { get; set; } = "";

        public string? Scope { get; set; }

        public string ProjectId { get; set; } = "";

        public string? ContextLabel { get; set; }

        public string? WorkspaceId { get; set; }

        public string? AgentId { get; set; }

        public long CreatedAt { get; set; }

        public int AccessCount { get; set; }

        public long? LastAccessedAt { get; set; }

        public double Rating { get; set; }

        public int? TtlDays { get; set; }

        public string EmbedState { get; set; } = "";
    }

    /// <summary>Deterministic test chunker: splits on blank lines.</summary>
    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
