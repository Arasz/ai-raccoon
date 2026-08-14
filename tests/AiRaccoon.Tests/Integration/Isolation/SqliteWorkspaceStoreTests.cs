using System.Linq;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Isolation;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteWorkspaceStoreTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteWorkspaceStore _store;

    public SqliteWorkspaceStoreTests()
    {
        _factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        _store = new SqliteWorkspaceStore(_factory);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task BeginAsync_InsertsActiveRow_WithCreatedAt()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.ProjectId.ShouldBe("acme");
        row.Status.ShouldBe(WorkspaceStatus.Active.ToString());
        row.CreatedAt.ShouldBe(startedAt.ToUnixTimeSeconds());
        row.ClosedAt.ShouldBeNull();
    }

    [Fact]
    public async Task BeginAsync_PersistsAgentIdAndName()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await _store.BeginAsync(new Workspace("ws-1", "acme", agentId: "agent-a", name: "refactor the parser"),
            startedAt, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.AgentId.ShouldBe("agent-a");
        row.Name.ShouldBe("refactor the parser");
    }

    [Fact]
    public async Task BeginAsync_WithoutProvenance_LeavesAgentIdAndNameNull()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.AgentId.ShouldBeNull();
        row.Name.ShouldBeNull();
    }

    [Fact]
    public async Task RequireActiveAsync_ReturnsTheStoredProvenance()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme", agentId: "agent-a", name: "refactor the parser"),
            startedAt, TestContext.Current.CancellationToken);

        var workspace = await _store.RequireActiveAsync("acme", "ws-1", TestContext.Current.CancellationToken);

        workspace.Id.ShouldBe("ws-1");
        workspace.ProjectId.ShouldBe("acme");
        workspace.Status.ShouldBe(WorkspaceStatus.Active);
        workspace.AgentId.ShouldBe("agent-a");
        workspace.Name.ShouldBe("refactor the parser");
    }

    [Fact]
    public async Task CloseAsync_WithDiscarded_RecordsThatTerminalStatus()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        await _store.CloseAsync("acme", "ws-1", WorkspaceStatus.Discarded, startedAt.AddHours(1),
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.Status.ShouldBe("Discarded");
    }

    [Fact]
    public async Task CloseAsync_MarksRowClosed_WithClosedAt()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var closedAt = startedAt.AddHours(2);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

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
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        await _store.CloseAsync("other", "ws-1", WorkspaceStatus.Closed, startedAt,
            TestContext.Current.CancellationToken);

        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.Status.ShouldBe(WorkspaceStatus.Active.ToString());
    }

    // ── WP5b/A-F7: the TOCTOU close, guarded by an atomic conditional UPDATE ──

    [Fact]
    public async Task CloseAsync_WithActiveStatus_Throws()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _store.CloseAsync("acme", "ws-1", WorkspaceStatus.Active, startedAt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryCloseAsync_WithActiveStatus_Throws()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _store.TryCloseAsync("acme", "ws-1", WorkspaceStatus.Active, startedAt, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryCloseAsync_OnAnActiveWorkspace_ClosesIt_AndReturnsTrue()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        var claimed = await _store.TryCloseAsync("acme", "ws-1", WorkspaceStatus.Closed, startedAt.AddHours(1),
            TestContext.Current.CancellationToken);

        claimed.ShouldBeTrue();
        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.Status.ShouldBe("Closed");
        row.ClosedAt.ShouldBe(startedAt.AddHours(1).ToUnixTimeSeconds());
    }

    [Fact]
    public async Task TryCloseAsync_OnAnAlreadyTerminalWorkspace_ReturnsFalse_AndDoesNotOverwriteTheRow()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);
        await _store.TryCloseAsync("acme", "ws-1", WorkspaceStatus.Discarded, startedAt.AddHours(1),
            TestContext.Current.CancellationToken);

        var secondClaim = await _store.TryCloseAsync("acme", "ws-1", WorkspaceStatus.Closed, startedAt.AddHours(2),
            TestContext.Current.CancellationToken);

        secondClaim.ShouldBeFalse();
        var row = await ReadRowAsync("ws-1");
        row.ShouldNotBeNull();
        row.Status.ShouldBe("Discarded", "the second, losing caller must not overwrite the first terminal status");
        row.ClosedAt.ShouldBe(startedAt.AddHours(1).ToUnixTimeSeconds(), "nor its closed_at");
    }

    [Fact]
    public async Task TryCloseAsync_OnANonExistentWorkspace_ReturnsFalse()
    {
        var claimed = await _store.TryCloseAsync("acme", "ghost", WorkspaceStatus.Closed,
            new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), TestContext.Current.CancellationToken);

        claimed.ShouldBeFalse();
    }

    /// <summary>The RED case named by the acceptance criteria: two callers racing to close the same
    /// workspace concurrently, not just sequentially — exactly one must win.</summary>
    [Fact]
    public async Task TryCloseAsync_ConcurrentClaims_OnlyOneSucceeds()
    {
        var startedAt = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
        await _store.BeginAsync(new Workspace("ws-1", "acme"), startedAt, TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(
            _store.TryCloseAsync("acme", "ws-1", WorkspaceStatus.Closed, startedAt, TestContext.Current.CancellationToken),
            _store.TryCloseAsync("acme", "ws-1", WorkspaceStatus.Discarded, startedAt, TestContext.Current.CancellationToken));

        results.Count(r => r).ShouldBe(1, "exactly one concurrent close must win");
    }

    private async Task<WorkspaceRow?> ReadRowAsync(string workspaceId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QueryFirstOrDefaultAsync<WorkspaceRow>(
            new CommandDefinition(
                "SELECT id AS Id, project_id AS ProjectId, agent_id AS AgentId, name AS Name, status AS Status, " +
                "created_at AS CreatedAt, closed_at AS ClosedAt FROM workspaces WHERE id = @workspaceId",
                new { workspaceId }));
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");

    private sealed class WorkspaceRow
    {
        public string Id { get; set; } = "";

        public string ProjectId { get; set; } = "";

        public string? AgentId { get; set; }

        public string? Name { get; set; }

        public string Status { get; set; } = "";

        public long CreatedAt { get; set; }

        public long? ClosedAt { get; set; }
    }
}
