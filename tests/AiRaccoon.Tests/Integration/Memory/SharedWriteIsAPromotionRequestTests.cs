using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Memory;

/// <summary>
///     WP2 (docs/adr/0067). An agent naming `shared` is asking for the row to be promoted, and that
///     is the strongest promotion signal available — better than any scorer inferring it. Today the
///     write lands directly in the shared tier at the default `rw` mode, crossing the project
///     boundary with no review, and no promotion candidate is recorded at all.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SharedWriteIsAPromotionRequestTests : IDisposable
{
    private const string ProjectId = "acme";
    private const string Content = "The discard purge requires age and an absent entry.";

    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("shared-write-promotion");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakePromotionQueue _queue = new();
    private readonly IMemoryWriteService _writes;
    private readonly SqliteMemoryStore _store;

    public SharedWriteIsAPromotionRequestTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        // A recording queue, not the real graph: this gate is about what the WRITE path decides.
        // That a proposed candidate persists is PromotionQueueService's own contract and its tests.
        _writes = new MemoryWriteService(_store, _queue);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The boundary: a `shared` write must not create a shared row. Before the fix it created one
    ///     and no queue row, so the request crossed the project boundary AND was never reviewed.
    /// </summary>
    [RetryFact]
    public async Task SharedWrite_CreatesNoSharedRow_AndQueuesAPromotionCandidate()
    {
        var entry = await _writes.WriteAsync(
            new MemoryWriteRequest(ProjectId, Content) { Context = ContextNaming.SharedContext },
            TestContext.Current.CancellationToken);

        entry.Stored.ShouldBeTrue("the write is never lost — it becomes a project row plus a request");

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var shared = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM entries WHERE scope = 'shared'");
        shared.ShouldBe(0, "naming `shared` asks for promotion; it does not perform one");

        var mine = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM entries WHERE scope = 'project' AND project_id = @p", new { p = ProjectId });
        mine.ShouldBe(1, "the row lands in the caller's own project and is searchable immediately");

        _queue.LastProject.ShouldBe(ProjectId);
        var candidate = _queue.LastCandidates.ShouldHaveSingleItem();
        candidate.Hash.ShouldBe(entry.Hash, "the candidate is the row that was just written");
        candidate.Reasons.ShouldContain(PromotionReasons.AgentRequestedShare,
            "an agent-requested candidate must be distinguishable from a scorer-proposed one");
        entry.Reason!.ShouldContain(PromotionReasons.AgentRequestedShare);
    }

    /// <summary>An ordinary write is untouched: no queue row, no change of scope.</summary>
    [RetryFact]
    public async Task OrdinaryWrite_QueuesNothing()
    {
        await _writes.WriteAsync(new MemoryWriteRequest(ProjectId, Content),
            TestContext.Current.CancellationToken);

        _queue.LastCandidates.ShouldBeNull("only an explicit `shared` write is a promotion request");
    }

    /// <summary>
    ///     K5 (owner ruling, F24): supplying an explicit context inside an active workspace must not
    ///     bypass the sandbox. The row lands in the outbox, not in the named context and not
    ///     project-wide, and no promotion candidate is minted for scratch content.
    /// </summary>
    [RetryFact]
    public async Task WorkspaceWrite_WithAnExplicitContext_LandsInTheOutbox_AndQueuesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = await BeginWorkspaceAsync(ct);

        var entry = await _writes.WriteAsync(
            new MemoryWriteRequest(ProjectId, Content, Context: "design-notes", WorkspaceId: workspace.Id), ct);

        entry.Stored.ShouldBeTrue();
        entry.Context.ShouldBe($"workspace:{workspace.Id}", "the sandbox has priority over the explicit context");

        await using var connection = await _factory.OpenBankAsync(ct);
        var rows = await connection.QueryAsync<(string? Scope, string? ContextLabel, string? WorkspaceId)>(
            "SELECT scope AS Scope, context_label AS ContextLabel, workspace_id AS WorkspaceId FROM entries");
        var row = rows.ShouldHaveSingleItem();
        row.WorkspaceId.ShouldBe(workspace.Id);
        row.Scope.ShouldBeNull();
        row.ContextLabel.ShouldBeNull("the context label is dropped when the workspace wins");
        _queue.LastCandidates.ShouldBeNull("scratch content is not a promotion request");
    }

    /// <summary>
    ///     The measured F24 case with `context=shared`: before the fix the shared rewrite won and the
    ///     row landed project-wide plus a promotion candidate, with the outbox empty. The workspace
    ///     now wins entirely — outbox row, no shared row, no project row, no candidate.
    /// </summary>
    [RetryFact]
    public async Task WorkspaceWrite_WithSharedContext_LandsInTheOutbox_AndQueuesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = await BeginWorkspaceAsync(ct);

        var entry = await _writes.WriteAsync(
            new MemoryWriteRequest(ProjectId, Content, Context: ContextNaming.SharedContext, WorkspaceId: workspace.Id), ct);

        entry.Stored.ShouldBeTrue();
        entry.Context.ShouldBe($"workspace:{workspace.Id}", "the sandbox has priority over the shared request");
        entry.Reason.ShouldBeNull("a workspace write is scratch, never a promotion request");

        await using var connection = await _factory.OpenBankAsync(ct);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries WHERE scope = 'shared'"))
            .ShouldBe(0, "naming `shared` inside a workspace must not create a shared row");
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries WHERE scope = 'project'"))
            .ShouldBe(0, "the workspace wins over the project rewrite the shared path used to apply");
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM entries WHERE workspace_id = @ws",
                new { ws = workspace.Id }))
            .ShouldBe(1, "the row is in the outbox");
        _queue.LastCandidates.ShouldBeNull("no promotion candidate is minted for scratch content");
    }

    private async Task<Workspace> BeginWorkspaceAsync(CancellationToken cancellationToken)
    {
        var workspaces = new WorkspaceService(_store, new SqliteWorkspaceStore(_factory), new FakeTimeProvider(FixedNow));
        return await workspaces.BeginAsync(ProjectId, cancellationToken: cancellationToken);
    }
}
