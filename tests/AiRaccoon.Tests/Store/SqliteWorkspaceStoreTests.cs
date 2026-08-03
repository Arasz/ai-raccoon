using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

public sealed class SqliteWorkspaceStoreTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWorkspaceStore _store;

    public SqliteWorkspaceStoreTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadExtensions: _ => { });
        _store = new SqliteWorkspaceStore(_factory);
    }

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    [Fact]
    public async Task BeginAsync_InsertsActiveRow_WithCreatedAt()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await _store.BeginAsync("acme", "ws-1", startedAt, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.ProjectId.ShouldBe("acme");
        row.Status.ShouldBe(WorkspaceStatus.Active.ToString());
        row.CreatedAt.ShouldBe(startedAt.ToUnixTimeSeconds());
        row.ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task CloseAsync_MarksRowClosed_WithClosedAt()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var closedAt = startedAt.AddHours(2);
        await _store.BeginAsync("acme", "ws-1", startedAt, TestContext.Current.CancellationToken);

        await _store.CloseAsync("acme", "ws-1", WorkspaceStatus.Closed, closedAt,
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.Status.ShouldBe(WorkspaceStatus.Closed.ToString());
        row.ClosedAt.ShouldBe(closedAt.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task CloseAsync_ForAnotherProject_DoesNotTouchTheRow()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync("acme", "ws-1", startedAt, TestContext.Current.CancellationToken);

        await _store.CloseAsync("other", "ws-1", WorkspaceStatus.Closed, startedAt,
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.Status.ShouldBe(WorkspaceStatus.Active.ToString());
    }

    private async Task<WorkspaceRow?> ReadRowAsync(string workspaceId)
    {
        await using var connection = await _factory.OpenMetaAsync(TestContext.Current.CancellationToken);
        return await connection.QueryFirstOrDefaultAsync<WorkspaceRow>(
            new CommandDefinition(
                "SELECT workspace_id AS WorkspaceId, project_id AS ProjectId, status AS Status, " +
                "created_at AS CreatedAt, closed_at AS ClosedAt FROM workspaces WHERE workspace_id = @workspaceId",
                new { workspaceId }));
    }

    private sealed class WorkspaceRow
    {
        public string WorkspaceId { get; set; } = "";

        public string ProjectId { get; set; } = "";

        public string Status { get; set; } = "";

        public long CreatedAt { get; set; }

        public long? ClosedAt { get; set; }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-workspaces-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
