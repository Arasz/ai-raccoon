using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Real-SQLite proof that a write cannot land outside its own project, and that a labelled write
///     stores the same label the search filter looks for.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemoryStoreContextScopeTests : IDisposable
{
    private const string Caller = "acme";
    private const string Victim = "victim";

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-context-scope");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public MemoryStoreContextScopeTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)), TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task AddContentAsync_NamingAnotherProjectInTheContext_WritesNothing()
    {
        // The access gate authorises the request's projectId, so a context that re-targets the row
        // would place it in a project the caller was never cleared for.
        await Should.ThrowAsync<ContextOutsideProjectException>(() => _store.AddContentAsync(
            Caller, "/notes/leak.md", "planted", ContextNaming.ProjectContext(Victim),
            cancellationToken: TestContext.Current.CancellationToken));

        (await RowsForProjectAsync(Victim)).ShouldBe(0, "the refused write must leave no trace");
        (await RowsForProjectAsync(Caller)).ShouldBe(0);
    }

    [Fact]
    public async Task AddContentAsync_WithTheCallersOwnProjectContext_Writes()
    {
        await _store.AddContentAsync(Caller, "/notes/ours.md", "kept",
            ContextNaming.ProjectContext(Caller), cancellationToken: TestContext.Current.CancellationToken);

        (await RowsForProjectAsync(Caller)).ShouldBe(1);
    }

    [Fact]
    public async Task AddContentAsync_WithALabelContext_StoresTheLabelTheFilterLooksFor()
    {
        // The stored context_label must equal what ContextKeyFor puts in the vec0 `ctx` column,
        // or the row is reachable by vector search and invisible to keyword search.
        var context = ContextNaming.LabelContext(Caller, "docs");

        await _store.AddContentAsync(Caller, "/notes/labelled.md", "labelled",
            context, cancellationToken: TestContext.Current.CancellationToken);

        var stored = await StoredLabelAsync();
        stored.ShouldBe("docs");
        MemorySql.ContextKeyFor(context, Caller).ShouldEndWith($":{stored}");
    }

    [Fact]
    public async Task DeleteContextAsync_NamingAnotherProject_DeletesNothing()
    {
        // The access gate authorises the request's projectId; a context that re-targets the delete
        // would destroy rows in a project the caller was never cleared for.
        await _store.AddContentAsync(Victim, "/notes/theirs.md", "theirs",
            ContextNaming.ProjectContext(Victim), cancellationToken: TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ContextOutsideProjectException>(() => _store.DeleteContextAsync(
            Caller, ContextNaming.ProjectContext(Victim), TestContext.Current.CancellationToken));

        (await RowsForProjectAsync(Victim)).ShouldBe(1, "the refused delete must leave the victim's row");
    }

    [Fact]
    public async Task DeleteContextAsync_NamingTheSharedTier_DeletesNothing()
    {
        // `shared` names no project, so a filter built from it carries no project predicate and
        // would sweep every project's promoted rows.
        await _store.AddContentAsync(Victim, "/notes/promoted.md", "promoted",
            ContextNaming.SharedContext, cancellationToken: TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ContextOutsideProjectException>(() => _store.DeleteContextAsync(
            Caller, ContextNaming.SharedContext, TestContext.Current.CancellationToken));

        (await SharedRowsAsync()).ShouldBe(1, "the refused delete must leave the shared tier intact");
    }

    [Fact]
    public async Task DeleteContextAsync_WithTheCallersOwnProjectContext_Deletes()
    {
        await _store.AddContentAsync(Caller, "/notes/ours.md", "ours",
            ContextNaming.ProjectContext(Caller), cancellationToken: TestContext.Current.CancellationToken);

        var deleted = await _store.DeleteContextAsync(
            Caller, ContextNaming.ProjectContext(Caller), TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);
        (await RowsForProjectAsync(Caller)).ShouldBe(0);
    }

    private async Task<int> SharedRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM entries WHERE scope = 'shared'");
    }

    private async Task<int> RowsForProjectAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM entries WHERE project_id = @projectId", new { projectId });
    }

    private async Task<string?> StoredLabelAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT context_label FROM entries WHERE context_label IS NOT NULL LIMIT 1");
    }
}
