using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

public class SqliteMemoryStoreTests
{
    [Fact]
    public void InsertTextStatement_IsMemoryAddText()
    {
        MemorySql.InsertText.ShouldBe("SELECT memory_add_text(@content, @context)");
    }

    [Fact]
    public void SelectEntryByHashStatement_ReadsDbmemContentColumns()
    {
        MemorySql.SelectEntryByHash.ShouldBe(
            "SELECT hash, path, context, value, created_at FROM dbmem_content WHERE hash = @hash");
    }

    [Fact]
    public void SearchStatement_WithContext_FiltersByContextColumn()
    {
        MemorySql.SearchWithContext.ShouldContain("AND context = @context");
        MemorySql.SearchWithContext.ShouldContain("ORDER BY ranking DESC");
    }

    [Fact]
    public void ShareSourceStatement_ReadsSourceRowByHashAndProjectContext()
    {
        MemorySql.SelectSourceByHashAndContext.ShouldContain("WHERE hash = @hash");
        MemorySql.SelectSourceByHashAndContext.ShouldContain("AND context = @context");
        MemorySql.SelectSourceByHashAndContext.ShouldContain("LIMIT 1");
    }

    [Fact]
    public void CommittedContextsStatement_SpansSharedAndProjectContextsOnly()
    {
        MemorySql.CommittedContexts.ShouldContain("context = 'shared'");
        MemorySql.CommittedContexts.ShouldContain("LIKE 'project:%'");
        MemorySql.CommittedContexts.ShouldNotContain("workspace");
    }

    [Fact]
    public void DeleteStatement_IsMemoryDelete()
    {
        MemorySql.Delete.ShouldBe("SELECT memory_delete(@hash)");
    }

    [Fact]
    public void DeleteContextStatement_IsMemoryDeleteContext()
    {
        MemorySql.DeleteContext.ShouldBe("SELECT memory_delete_context(@context)");
    }

    [Fact]
    public void StatsStatements_CountEntriesAndPending()
    {
        MemorySql.CountEntries.ShouldBe("SELECT count(*) FROM dbmem_content");
        MemorySql.PendingCount.ShouldBe("SELECT memory_pending_count()");
    }

    [Fact]
    public async Task Dapper_MapsAliasedColumns_IntoMemoryEntry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE dbmem_content (hash TEXT, path TEXT, context TEXT, value TEXT, created_at INTEGER)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO dbmem_content VALUES (@hash, @path, @context, @value, @createdAt)";
            insert.Parameters.AddWithValue("@hash", "abc123");
            insert.Parameters.AddWithValue("@path", "notes.md");
            insert.Parameters.AddWithValue("@context", "project:acme");
            insert.Parameters.AddWithValue("@value", "remember this");
            insert.Parameters.AddWithValue("@createdAt", 1_752_000_000L);
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // The store's Dapper reads alias snake_case columns to record constructor names;
        // this asserts that a query using those aliases materializes a MemoryEntry.
        var entry = await connection.QueryFirstOrDefaultAsync<MemoryEntry>(
            "SELECT hash AS Hash, path AS Path, context AS Context, value AS Value, created_at AS CreatedAt FROM dbmem_content",
            TestContext.Current.CancellationToken);

        entry.ShouldBe(new MemoryEntry("abc123", "notes.md", "project:acme", "remember this", 1_752_000_000L));
    }

    [Fact]
    public void SearchContexts_AllScope_SpansSharedProjectAndNamedWorkspace()
    {
        var query = new SearchQuery("acme", "q", scope: SearchScope.All, workspaceId: "ws-1");

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
        var query = new SearchQuery("acme", "q", scope: SearchScope.Project, workspaceId: "ws-1");

        SearchContexts.For(query).ShouldBe(
            [ContextNaming.ProjectContext("acme"), ContextNaming.WorkspaceContext("ws-1")]);
    }

    [Fact]
    public void SearchContexts_ProjectScope_WithoutWorkspace_IsProjectOnly()
    {
        var query = new SearchQuery("acme", "q", scope: SearchScope.Project);

        SearchContexts.For(query).ShouldBe([ContextNaming.ProjectContext("acme")]);
    }

    [Fact]
    public void SearchContexts_SharedScope_IsSharedOnly_EvenWhenWorkspaceNamed()
    {
        var query = new SearchQuery("acme", "q", scope: SearchScope.Shared, workspaceId: "ws-1");

        SearchContexts.For(query).ShouldBe([ContextNaming.SharedContext]);
    }

    [Fact]
    public void Merge_KeepsBestRankingPerHash_AcrossContextBatches()
    {
        var shared = new[]
            { new MemorySearchResult("h1", 1, 0.8, "a.md", "s"), new MemorySearchResult("h2", 2, 0.6, "b.md", "s") };
        var project = new[]
            { new MemorySearchResult("h1", 1, 0.9, "a.md", "s"), new MemorySearchResult("h3", 3, 0.5, "c.md", "s") };

        var merged = SearchResultMerger.Merge([shared, project], limit: 10);

        merged.Select(r => r.Hash).ShouldBe(["h1", "h2", "h3"]);
        merged[0].Ranking.ShouldBe(0.9);
    }

    [Fact]
    public void Merge_SortsByRankingDescending_AndLimits()
    {
        var results = new[]
        {
            new MemorySearchResult("h1", 1, 0.4, "a.md", "s"),
            new MemorySearchResult("h2", 2, 0.9, "b.md", "s"),
            new MemorySearchResult("h3", 3, 0.7, "c.md", "s"),
        };

        var merged = SearchResultMerger.Merge([results], limit: 2);

        merged.Select(r => r.Hash).ShouldBe(["h2", "h3"]);
    }
}
