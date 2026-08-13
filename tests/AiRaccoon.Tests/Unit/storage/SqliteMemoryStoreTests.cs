using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

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
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            new EmbeddingService());
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private async Task EnsureWorkspaceAsync(string workspaceId, string projectId = "acme")
    {
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync(new Workspace(workspaceId, projectId), FixedNow, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetProjectIdsAsync_ReturnsDistinctOrderedProjectScopeIdsOnly()
    {
        await _store.WriteAsync(
            new MemoryWriteRequest("beta", "beta committed fact"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "acme committed fact"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(
            new MemoryWriteRequest("gamma", "gamma committed fact"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "another acme fact"),
            TestContext.Current.CancellationToken);

        var sharedEntry = await _store.ShareAsync("beta", (await _store.WriteAsync(
            new MemoryWriteRequest("beta", "promoted to shared"),
            TestContext.Current.CancellationToken)).Hash, TestContext.Current.CancellationToken);
        sharedEntry.Entry.Context.ShouldBe(ContextNaming.SharedContext);

        await EnsureWorkspaceAsync("ws-1");

        var projects = await _store.GetProjectIdsAsync(TestContext.Current.CancellationToken);

        projects.ShouldBe(["acme", "beta", "gamma"]);
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
            new MemoryWriteRequest("acme", "draft finding", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("workspace:ws-1");

        var row = await ReadRowAsync(entry.Hash);
        row.ShouldNotBeNull();
        row.WorkspaceId.ShouldBe("ws-1");
        row.Scope.ShouldBeNull();
    }

    [Fact]
    public async Task Write_WithNonexistentWorkspaceId_ThrowsUnknownWorkspaceException()
    {
        var ex = await Should.ThrowAsync<UnknownWorkspaceException>(() =>
            _store.WriteAsync(new MemoryWriteRequest("acme", "draft finding", WorkspaceId: "ghost"),
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ghost");
        ex.Message.ShouldContain("acme");
    }

    [Fact]
    public async Task Write_WithDiscardedWorkspaceId_ThrowsUnknownWorkspaceException()
    {
        // A stale workspaceId (post-close) must fail an active-status check, not merely a
        // row-existence check — the workspaces row survives close (see IWorkspaceStore).
        await EnsureWorkspaceAsync("ws-1");
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.CloseAsync("acme", "ws-1", WorkspaceStatus.Closed, FixedNow,
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<UnknownWorkspaceException>(() =>
            _store.WriteAsync(new MemoryWriteRequest("acme", "stale write", WorkspaceId: "ws-1"),
                TestContext.Current.CancellationToken));
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
        // Hybrid-search contract: ranking is normalized into 0..1 with the top result at 1.0.
        hit.Ranking.ShouldBeInRange(0.0, 1.0);
        hit.Ranking.ShouldBe(1.0);
    }

    [Fact]
    public async Task Search_WithoutEmbeddingEngine_KeywordOnlyQuery_ReturnsKeywordResultsAboveMinScore()
    {
        // No embedding engine means the vec modality is absent (docs/work/features-native-memory/native-memory.feature);
        // the keyword query must still return results above the minimum score without crashing.
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "the only exact keyword phrase present is ziggurat"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "ziggurat", MinScore: 0.7),
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
        metadata.Rating.ShouldBe(row.Rating);
    }

    /// <summary>SelectRatingForBump/BumpAccess must not touch another project's row that happens
    /// to share the same content hash — searching one project must never age or rate-bump another's.</summary>
    [Fact]
    public async Task Search_BumpsAccessOnlyForTheSearchedProjectsRow_NotAnotherProjectsIdenticalHash()
    {
        var acme = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "identical fact shared by two projects"),
            TestContext.Current.CancellationToken);
        var other = await _store.WriteAsync(
            new MemoryWriteRequest("other", "identical fact shared by two projects"),
            TestContext.Current.CancellationToken);
        other.Hash.ShouldBe(acme.Hash, "the collision this test needs: identical content, two projects");

        await _store.SearchAsync(new SearchQuery("acme", "identical"), TestContext.Current.CancellationToken);

        var acmeRow = await ReadRowByProjectAsync("acme", acme.Hash);
        acmeRow.ShouldNotBeNull();
        acmeRow.AccessCount.ShouldBe(1);

        var otherRow = await ReadRowByProjectAsync("other", other.Hash);
        otherRow.ShouldNotBeNull();
        otherRow.AccessCount.ShouldBe(0, "searching acme must not bump other's identically-hashed row");
        otherRow.Rating.ShouldBe(RatingPolicy.DefaultBaseScore);
    }

    [Fact]
    public async Task Share_CopiesRowIntoSharedScope_PreservingPath()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "cross project convention"),
            TestContext.Current.CancellationToken);

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        shared.Entry.Context.ShouldBe(ContextNaming.SharedContext);
        shared.Entry.Path.ShouldStartWith("shared/");
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
    public async Task ShareAsync_WithUnknownHash_ThrowsUnknownHashException()
    {
        var ex = await Should.ThrowAsync<UnknownHashException>(() =>
            _store.ShareAsync("acme", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd",
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd");
        ex.Message.ShouldContain("acme");
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
                new SearchQuery("acme", "deleted", MinScore: 0),
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
        await _store.WriteAsync(new MemoryWriteRequest("acme", "workspace draft", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "committed fact"), TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteContextAsync("acme", "workspace:ws-1", TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);
        (await _store.ListContextAsync("acme", "workspace:ws-1", TestContext.Current.CancellationToken)).ShouldBeEmpty();
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteContext_WithAnUnknownContext_ReturnsZero()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "committed fact"), TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteContextAsync("acme", "workspace:does-not-exist",
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(0);
        (await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken)).EntryCount.ShouldBe(1);
    }

    [Fact]
    public async Task Stats_CountsCommittedEntries_AndPendingFromEmbedState()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "committed fact"), TestContext.Current.CancellationToken);
        await EnsureWorkspaceAsync("ws-1");
        await _store.WriteAsync(new MemoryWriteRequest("acme", "workspace draft", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var stats = await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken);

        stats.EntryCount.ShouldBe(1);
        stats.PendingCount.ShouldBe(2);
        stats.Contexts.ShouldContain("project:acme");
    }

    [Fact]
    public async Task Stats_ScopesContextsToTheCallingProject_PlusShared()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "acme fact"), TestContext.Current.CancellationToken);
        var betaEntry = await _store.WriteAsync(new MemoryWriteRequest("beta", "beta fact"),
            TestContext.Current.CancellationToken);
        await _store.ShareAsync("beta", betaEntry.Hash, TestContext.Current.CancellationToken);

        var stats = await _store.GetStatsAsync("acme", TestContext.Current.CancellationToken);

        stats.Contexts.ShouldBe(["shared", "project:acme"]);
    }

    [Fact]
    public async Task AddContent_IsIdempotent_ByPathInBucket()
    {
        var first = await _store.AddContentAsync("acme", "docs/note.md", "note content", null,
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await _store.AddContentAsync("acme", "docs/note.md", "note content", null,
            cancellationToken: TestContext.Current.CancellationToken);

        second.Entry.Hash.ShouldBe(first.Entry.Hash);
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

        shared.Entry.Context.ShouldBe(ContextNaming.SharedContext);
        shared.Entry.Path.ShouldBe($"shared/{ContentHash.OfValue(entry.Value)}.md");
        shared.Entry.Value.ShouldBe(entry.Value);
        shared.Entry.Hash.ShouldNotBe(entry.Hash);
        shared.Entry.Hash.ShouldBe(ContentHash.Of($"shared/{ContentHash.OfValue(entry.Value)}.md", entry.Value));
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
            cancellationToken: TestContext.Current.CancellationToken);

        var committed = await _store.AddContentAsync("acme", "docs/note.md", "workspace draft",
            ContextNaming.ProjectContext("acme"), cancellationToken: TestContext.Current.CancellationToken);

        committed.Entry.Context.ShouldBe("project:acme");
        committed.Entry.Path.ShouldBe("docs/note.md");
        committed.Entry.Hash.ShouldBe(ContentHash.Of("docs/note.md", "workspace draft"));
    }

    [Fact]
    public async Task ListFiles_ReturnsJsonTree_FromEntryPaths()
    {
        await _store.AddContentAsync("acme", "docs/guide.md", "guide", null, cancellationToken: TestContext.Current.CancellationToken);
        await _store.AddContentAsync("acme", "notes.md", "notes", null, cancellationToken: TestContext.Current.CancellationToken);

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

        result.Processed.ShouldBe(0); // no engine configured → nothing can be embedded (docs/work/features-native-memory/native-memory.feature)
        result.Pending.ShouldBe(1);
    }

    [Fact]
    public async Task ConfigureEmbedding_StoresProviderAndModel_InSettings()
    {
        var config = await _store.ConfigureEmbeddingAsync("openai", "nomic-embed-text", null,
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

        await AllowIngestScopeAsync(_dataRoot);

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

        await AllowIngestScopeAsync(_dataRoot);

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

    [Fact]
    public void SearchContexts_ProjectScope_WithContextLabel_AddsTheLabelContext()
    {
        var query = new SearchQuery("acme", "q", SearchScope.Project, ContextLabel: "docs:adr");

        SearchContexts.For(query).ShouldBe(
            [ContextNaming.ProjectContext("acme"), ContextNaming.LabelContext("acme", "docs:adr")]);
    }

    [Fact]
    public async Task Write_WithSourceFile_StoresColumn_AndSearchCarriesIdentity()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "ADR-0070 decides documentation placement",
                SourceFile: "docs/adr/0070-documentation-structure-and-trust-model.md", Section: "decision"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "documentation placement", SearchScope.Project, Limit: 5, MinScore: 0.0),
            TestContext.Current.CancellationToken);

        var hit = results.ShouldHaveSingleItem();
        hit.Hash.ShouldBe(entry.Hash);
        hit.SourceFile.ShouldBe("docs/adr/0070-documentation-structure-and-trust-model.md");
        hit.TotalChunks.ShouldBe(1);
        hit.ChunkIndex.ShouldBe(0);
    }

    [Fact]
    public async Task Write_WithSection_IndexesSectionInFts_AndSectionQueriesMatchIt()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "the widget renderer decision",
                SourceFile: "docs/adr/0099-widget-renderer.md", Section: "decision"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "docs/adr/0099-widget-renderer.md#decision",
                SearchScope.Project, Limit: 5, MinScore: 0.0),
            TestContext.Current.CancellationToken);

        results.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Search_WithoutContextLabel_ExcludesCustomScopedRows()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "docs only fact", "docs:adr"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "docs only fact", SearchScope.Project, Limit: 5, MinScore: 0.0),
            TestContext.Current.CancellationToken);

        results.ShouldBeEmpty("scope='custom' rows are invisible to the plain project scope");
    }

    [Fact]
    public async Task Search_WithContextLabel_IncludesCustomScopedRows_AlongsideProjectRows()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "custom labeled fact", "docs:adr"),
            TestContext.Current.CancellationToken);
        await _store.WriteAsync(new MemoryWriteRequest("acme", "plain project fact"),
            TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", "fact", SearchScope.Project, Limit: 5, MinScore: 0.0,
                ContextLabel: "docs:adr"),
            TestContext.Current.CancellationToken);

        results.Select(r => r.Snippet).ShouldContain(s => s.Contains("custom labeled fact", StringComparison.Ordinal));
        results.Select(r => r.Snippet).ShouldContain(s => s.Contains("plain project fact", StringComparison.Ordinal),
            "the context label filter augments the project scope, it does not replace it");
    }

    /// <summary>
    ///     Regression for the double-RRF defect (see docs/adr/0006-rrf-parameter-optimization.md):
    ///     the outer merge used to fuse per-context batches by rank POSITION, so a shared tier
    ///     holding a single unrelated entry always tied the project tier's genuine top match at
    ///     "rank 1 of my own context" — a tie broken only by Path.
    /// </summary>
    [Fact]
    public async Task Search_ScopeAll_SmallSharedTier_DoesNotCaptureAnUnrelatedQuery()
    {
        const string query = "docker container deployment rollback pipeline";

        // Path deliberately sorts after "shared/..." (Ordinal) so the old code's Path tie-break
        // cannot accidentally save this assertion — only a genuine score wins it.
        var bestMatch = await _store.AddContentAsync("acme", "zz-deploy-pipeline-decision.md",
            "The docker container deployment pipeline automatically triggers a rollback when health checks fail three times.",
            ContextNaming.ProjectContext("acme"), cancellationToken: TestContext.Current.CancellationToken);

        string[] noise =
        [
            "CI pipeline runs unit tests before merging any pull request.",
            "The build system caches docker layers to speed up image creation.",
            "Kubernetes restarts a container when its readiness probe fails.",
            "Deployment history is stored so operators can audit each release.",
            "The staging environment mirrors production for pre-release checks.",
            "A canary release shifts five percent of traffic to the new version.",
            "Docker images are scanned for known vulnerabilities before publishing.",
            "The release manager tags each build with a semantic version number.",
            "Blue-green deployment swaps traffic between two identical environments.",
            "Container orchestration schedules workloads across the available nodes.",
            "The deployment dashboard shows the health of every running service.",
            "Automated smoke tests run immediately after each deployment.",
            "The pipeline notifies the on-call engineer when a stage fails.",
            "Rollback plans are rehearsed quarterly during the reliability drill.",
            "The container registry retains the last ten images per service.",
            "Infrastructure changes go through the same review process as code."
        ];
        foreach (var content in noise)
        {
            await _store.WriteAsync(new MemoryWriteRequest("acme", content), TestContext.Current.CancellationToken);
        }

        var offTopic = await _store.WriteAsync(
            new MemoryWriteRequest("acme",
                "sqlite schema write guards reject any bank whose version exceeds what the pipeline validator supports"),
            TestContext.Current.CancellationToken);
        var shared = await _store.ShareAsync("acme", offTopic.Hash, TestContext.Current.CancellationToken);

        var results = await _store.SearchAsync(
            new SearchQuery("acme", query, SearchScope.All, Limit: 25, MinScore: 0.0),
            TestContext.Current.CancellationToken);

        results[0].Path.ShouldBe(bestMatch.Entry.Path,
            "the genuinely relevant project entry must rank first, not an unrelated single-entry shared tier");
        var sharedResult = results.Single(r => r.Hash == shared.Entry.Hash);
        sharedResult.Ranking.ShouldBeLessThan(results[0].Ranking,
            "a single promoted entry in the shared tier must not tie the project tier's real top match");
    }

    // Merge's own multi-list RRF behaviour (fuses batches by rank position); production now
    // passes it a single already-globally-fused batch (see docs/adr/0006-rrf-parameter-optimization.md).
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

        var merged = SearchResultMerger.Merge([results], 10, 0.9, 10);

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
    public void Merge_EmptyBatches_ReturnsEmpty() =>
        SearchResultMerger.Merge([[], []], 10)
            .ShouldBeEmpty();

    private static MemorySearchResult Hit(string hash, int seq, string path) => new(hash, seq, 0, path, "s");

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

    private async Task<EntryRow?> ReadRowByProjectAsync(string projectId, string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QueryFirstOrDefaultAsync<EntryRow>(
            """
            SELECT hash AS Hash, path AS Path, scope AS Scope, project_id AS ProjectId,
                   context_label AS ContextLabel, workspace_id AS WorkspaceId, agent_id AS AgentId,
                   created_at AS CreatedAt, access_count AS AccessCount, last_accessed_at AS LastAccessedAt,
                   rating AS Rating, ttl_days AS TtlDays, embed_state AS EmbedState
            FROM entries
            WHERE hash = @hash AND project_id = @projectId
            """,
            new { hash, projectId });
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

    [Fact]
    public async Task SetSetting_RoundTripsStructureAlpha()
    {
        await _store.SetSettingAsync(StructureFusion.AlphaSettingKey, "0.8",
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var raw = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT value FROM settings WHERE key = @key",
                new { key = StructureFusion.AlphaSettingKey },
                cancellationToken: TestContext.Current.CancellationToken));
        raw.ShouldBe("0.8", "the alpha setting must persist in the bank settings table");
    }

    [Fact]
    public async Task AddContentAsync_ConcurrentSameBucket_ExactlyOneCreated()
    {
        var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _store.AddContentAsync("acme", "p.md", "identical fact",
                null, cancellationToken: TestContext.Current.CancellationToken);
        })).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Entry.Hash).Distinct().ShouldHaveSingleItem();
        results.Count(r => r.Created).ShouldBe(1,
            "the ON CONFLICT DO NOTHING loser reports affected == 0 — exactly one caller created the row");
        results.Count(r => !r.Created).ShouldBe(1);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE path = 'p.md' AND scope = 'project'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task AddContentAsync_SecondSamePath_CreatedFalse()
    {
        var first = await _store.AddContentAsync("acme", "p.md", "identical fact",
            null, cancellationToken: TestContext.Current.CancellationToken);
        var second = await _store.AddContentAsync("acme", "p.md", "identical fact",
            null, cancellationToken: TestContext.Current.CancellationToken);

        first.Created.ShouldBeTrue("the first write creates the row");
        second.Created.ShouldBeFalse("the path pre-check finds the existing row — re-add is not a creation");
        second.Entry.Hash.ShouldBe(first.Entry.Hash, "the existing row is returned, not a fresh hash");
    }

    [Fact]
    public async Task ShareAsync_ConcurrentSameHash_SingleSharedRow()
    {
        var entry = await _store.WriteAsync(new MemoryWriteRequest("acme", "shared fact"),
            TestContext.Current.CancellationToken);
        await AllowIngestScopeAsync(_dataRoot);
        var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);
        })).ToArray();
        await Task.WhenAll(tasks);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE scope = 'shared'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task ShareAsync_ConcurrentSameHash_DifferentProjects_SingleSharedRow()
    {
        // The global shared index closes the cross-project promote race: project B's loser must
        // converge on project A's row (project-agnostic re-read), not throw.
        var entryA = await _store.WriteAsync(new MemoryWriteRequest("acme", "cross-project fact"),
            TestContext.Current.CancellationToken);
        var entryB = await _store.WriteAsync(new MemoryWriteRequest("beta", "cross-project fact"),
            TestContext.Current.CancellationToken);
        await AllowIngestScopeAsync(_dataRoot);
        var barrier = new Barrier(2);
        var tasks = new[]
        {
            Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await _store.ShareAsync("acme", entryA.Hash, TestContext.Current.CancellationToken);
            }),
            Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await _store.ShareAsync("beta", entryB.Hash, TestContext.Current.CancellationToken);
            })
        };
        await Task.WhenAll(tasks);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE scope = 'shared'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task WriteAsync_ConcurrentSameContent_SingleRowNoThrow()
    {
        var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _store.WriteAsync(new MemoryWriteRequest("acme", "identical write content"),
                TestContext.Current.CancellationToken);
        })).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Select(r => r.Hash).Distinct().ShouldHaveSingleItem();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE scope = 'project'",
            TestContext.Current.CancellationToken);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task IngestFileAsync_ConcurrentSameFile_SingleChunkSet()
    {
        var file = Path.Combine(_dataRoot, "multi.md");
        await File.WriteAllTextAsync(file, "chunk one\n\nchunk two\n\nchunk three",
            TestContext.Current.CancellationToken);
        await AllowIngestScopeAsync(_dataRoot);
        var barrier = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await _store.IngestFileAsync("acme", file, null,
                TestContext.Current.CancellationToken);
        })).ToArray();
        var results = await Task.WhenAll(tasks);

        // Convergence, not both-return-1: one racer may hit the exists-skip fast path (0) while
        // the other inserts the full set; the invariant is the single chunk set on disk, not the codes.
        results.ShouldContain(1);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE path = @file",
            new { file });
        count.ShouldBe(3, "one ingest's chunk set, not two");
    }

    // ── WP2: source_id on write path ──

    [Fact]
    public async Task WriteAsync_SetsSourceId_OnEntry()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "some fact about SQLite", SourceFile: "docs/db.md", Section: "## Storage"),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sourceId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT source_id FROM entries WHERE hash = @hash",
                new { hash = entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));
        sourceId.ShouldNotBeNull("WriteAsync must set source_id on the entry");
        sourceId!.Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task WriteAsync_NullSourceFile_SetsManualSource()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "a manual note"),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sourceId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT source_id FROM entries WHERE hash = @hash",
                new { hash = entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));
        sourceId.ShouldNotBeNull("WriteAsync must set source_id even for null source_file");

        var sourceType = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition(
                "SELECT source_type FROM memory_source WHERE id = @id",
                new { id = sourceId },
                cancellationToken: TestContext.Current.CancellationToken));
        sourceType.ShouldBe("manual");
    }

    [Fact]
    public async Task AddContentAsync_SetsSourceId_OnEntry()
    {
        var result = await _store.AddContentAsync(
            "acme", "shared/test.md", "shared content fact", ContextNaming.SharedContext,
            "docs/shared.md", "## Shared",
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sourceId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                "SELECT source_id FROM entries WHERE hash = @hash",
                new { hash = result.Entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));
        sourceId.ShouldNotBeNull("AddContentAsync must set source_id on the entry");
        sourceId!.Value.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task SelectSourceByHashAndProject_ReturnsSourceType()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "conversation fragment", SourceFile: "hermes/20260809_125502_abc123"),
            TestContext.Current.CancellationToken);

        var shared = await _store.ShareAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var sourceType = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition(
                """
                SELECT ms.source_type FROM entries e
                JOIN memory_source ms ON ms.id = e.source_id
                WHERE e.hash = @hash AND e.scope = 'shared'
                """,
                new { hash = shared.Entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));
        sourceType.ShouldBe("transcript",
            "SelectSourceByHashAndProject must resolve source_type via the memory_source JOIN");
    }

    [Fact]
    public async Task SelectExtractionCandidates_IncludesSourceType()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "architecture decision record content", SourceFile: "docs/adr/0001-decision.md"),
            TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                "UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash",
                new { hash = entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));

        var candidates = await _store.ExtractCandidatesAsync("acme", includeTtlRows: true, TestContext.Current.CancellationToken);

        var candidate = candidates.ShouldHaveSingleItem();
        candidate.Hash.ShouldBe(entry.Hash);
        candidate.SourceType.ShouldBe("file",
            "ExtractCandidatesAsync must return source_type from the memory_source JOIN");
    }

    /// <summary>Ingest is deny-by-default; these tests exercise chunking, not containment.</summary>
    private Task AllowIngestScopeAsync(string path) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([path]),
            TestContext.Current.CancellationToken);

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
    private sealed class StubChunker : IMarkdownChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
