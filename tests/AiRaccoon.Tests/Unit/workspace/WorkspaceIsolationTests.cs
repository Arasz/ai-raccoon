using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.Unit.workspace;

/// <summary>P8 schema and structural-isolation tests: FK + XOR CHECK on entries.workspace_id.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WorkspaceIsolationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;

    public WorkspaceIsolationTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private SqliteMemoryStore CreateStore() => new(_factory, new FakeTimeProvider(FixedNow), new StubChunker(), new EmbeddingService(),
            NullLogger<SqliteMemoryStore>.Instance);

    [Fact]
    public async Task XORCheck_InsertWithWorkspaceIdAndCommittedScope_Fails()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES (@id, @project, @status, @at)",
                new { id = "ws-1", project = "acme", status = "Active", at = 1L },
                cancellationToken: TestContext.Current.CancellationToken));

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at)
                    VALUES (@hash, @path, @value, @scope, @project, @ws, @at, @at)
                    """,
                    new { hash = "h1", path = "note.md", value = "draft", scope = "project", project = "acme", ws = "ws-1", at = 1L },
                    cancellationToken: TestContext.Current.CancellationToken));
        });

        ex.Message.ShouldContain("CHECK");
    }

    [Fact]
    public async Task XORCheck_UpdateToSetWorkspaceIdOnCommittedRow_Fails()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES (@id, @project, @status, @at)",
                new { id = "ws-1", project = "acme", status = "Active", at = 1L },
                cancellationToken: TestContext.Current.CancellationToken));

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at) VALUES (@hash, @path, @value, @scope, @project, NULL, @at, @at)",
                new { hash = "h1", path = "note.md", value = "fact", scope = "project", project = "acme", at = 1L },
                cancellationToken: TestContext.Current.CancellationToken));

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE entries SET workspace_id = @ws WHERE hash = @hash",
                    new { ws = "ws-1", hash = "h1" },
                    cancellationToken: TestContext.Current.CancellationToken));
        });

        ex.Message.ShouldContain("CHECK");
    }

    [Fact]
    public async Task FK_InsertWithMissingWorkspace_Fails()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at)
                    VALUES (@hash, @path, @value, NULL, @project, @ws, @at, @at)
                    """,
                    new { hash = "h1", path = "note.md", value = "draft", project = "acme", ws = "no-such-ws", at = 1L },
                    cancellationToken: TestContext.Current.CancellationToken));
        });

        ex.Message.ShouldContain("FOREIGN KEY");
    }

    [Fact]
    public async Task CompliantInsert_WorkspaceEntry_Succeeds()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES (@id, @project, @status, @at)",
                new { id = "ws-1", project = "acme", status = "Active", at = 1L },
                cancellationToken: TestContext.Current.CancellationToken));

        var exception = await Record.ExceptionAsync(async () =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at)
                    VALUES (@hash, @path, @value, NULL, @project, @ws, @at, @at)
                    """,
                    new { hash = "w1", path = "note.md", value = "draft", project = "acme", ws = "ws-1", at = 1L },
                    cancellationToken: TestContext.Current.CancellationToken));
        });

        exception.ShouldBeNull();
    }

    [Fact]
    public async Task CompliantInsert_CommittedEntry_Succeeds()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO entries (hash, path, value, scope, project_id, workspace_id, created_at, updated_at)
                    VALUES (@hash, @path, @value, @scope, @project, NULL, @at, @at)
                    """,
                    new { hash = "c1", path = "note.md", value = "fact", scope = "project", project = "acme", at = 1L },
                    cancellationToken: TestContext.Current.CancellationToken));
        });

        exception.ShouldBeNull();
    }

    [Fact]
    public async Task Write_WithWorkspaceId_SetsWorkspaceIdAndNullScope()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = CreateStore();

        // Create the workspace record first — FK requires it.
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync("acme", "ws-1", FixedNow, TestContext.Current.CancellationToken);

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "draft finding", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("workspace:ws-1");

        var row = await connection.QueryFirstOrDefaultAsync<EntryIsolationRow>(
            new CommandDefinition(
                "SELECT workspace_id AS WorkspaceId, scope AS Scope FROM entries WHERE hash = @hash",
                new { hash = entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));
        row.ShouldNotBeNull();
        row.WorkspaceId.ShouldBe("ws-1");
        row.Scope.ShouldBeNull();
    }

    [Fact]
    public async Task Write_WithoutWorkspaceId_SetsScopeAndNullWorkspaceId()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = CreateStore();

        var entry = await store.WriteAsync(
            new MemoryWriteRequest("acme", "project fact"),
            TestContext.Current.CancellationToken);

        entry.Context.ShouldBe("project:acme");

        var row = await connection.QueryFirstOrDefaultAsync<EntryIsolationRow>(
            new CommandDefinition(
                "SELECT workspace_id AS WorkspaceId, scope AS Scope FROM entries WHERE hash = @hash",
                new { hash = entry.Hash },
                cancellationToken: TestContext.Current.CancellationToken));
        row.ShouldNotBeNull();
        row.WorkspaceId.ShouldBeNull();
        row.Scope.ShouldBe("project");
    }

    [Fact]
    public async Task ConsolidateAsync_PromotesKept_DiscardsRest_ClosesWorkspace()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = CreateStore();
        var workspaceStore = new SqliteWorkspaceStore(_factory);

        // Create the workspace record first — FK requires it.
        await workspaceStore.BeginAsync("acme", "ws-1", FixedNow, TestContext.Current.CancellationToken);

        var service = new WorkspaceService(store, workspaceStore, new FakeTimeProvider(FixedNow));

        var e1 = await store.WriteAsync(
            new MemoryWriteRequest("acme", "durable fact", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);
        await store.WriteAsync(
            new MemoryWriteRequest("acme", "noise to discard", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var result = await service.ConsolidateAsync("acme", "ws-1", [e1.Hash], TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(1);
        result.Discarded.ShouldBeGreaterThan(0);

        _ = (await connection.QueryFirstOrDefaultAsync(
            new CommandDefinition(
                "SELECT hash FROM entries WHERE hash = @hash AND scope = 'project' AND workspace_id IS NULL",
                new { hash = e1.Hash },
                cancellationToken: TestContext.Current.CancellationToken)))!;

        var wsStatus = await connection.QueryFirstOrDefaultAsync<string>(
            new CommandDefinition(
                "SELECT status FROM workspaces WHERE id = 'ws-1'",
                cancellationToken: TestContext.Current.CancellationToken));
        wsStatus.ShouldBe("Closed");
    }

    [Fact]
    public async Task SweepService_StructurallyExcludesWorkspaceEntries()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = CreateStore();

        // Create the workspace record first — FK requires it.
        var workspaceStore = new SqliteWorkspaceStore(_factory);
        await workspaceStore.BeginAsync("acme", "ws-1", FixedNow, TestContext.Current.CancellationToken);

        await store.WriteAsync(
            new MemoryWriteRequest("acme", "committed fact"),
            TestContext.Current.CancellationToken);
        await store.WriteAsync(
            new MemoryWriteRequest("acme", "workspace draft", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        var committed = await store.ListContextAsync("acme", ContextNaming.ProjectContext("acme"),
            TestContext.Current.CancellationToken);
        committed.Count.ShouldBe(1);
        committed[0].Value.ShouldBe("committed fact");

        var workspace = await store.ListContextAsync("acme", ContextNaming.WorkspaceContext("ws-1"),
            TestContext.Current.CancellationToken);
        workspace.Count.ShouldBe(1);
        workspace[0].Value.ShouldBe("workspace draft");
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

    private sealed class EntryIsolationRow
    {
        public string? WorkspaceId { get; set; }
        public string? Scope { get; set; }
    }

    /// <summary>Deterministic test chunker: splits on blank lines.</summary>
    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
