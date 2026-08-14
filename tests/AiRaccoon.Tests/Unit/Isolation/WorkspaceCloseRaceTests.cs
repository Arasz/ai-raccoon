using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Workspace;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Isolation;

/// <summary>
///     A-F7 (WP5b, MEDIUM): <c>WorkspaceService.RequireActiveAsync</c> and <c>CloseAsync</c> each
///     opened their own connection, so a concurrent consolidate and discard on the same workspace
///     both passed the active-check and both wrote — last writer wins, and the outbox got consumed
///     twice (once promoted, once discarded, or both). This test runs the real service over real
///     SQLite (not a fake) to prove <see cref="WorkspaceService" />'s claim-first ordering, backed
///     by <see cref="SqliteWorkspaceStore" />'s atomic <c>TryCloseAsync</c>, actually closes the
///     race under genuine connection concurrency.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WorkspaceCloseRaceTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-workspace-close-race");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _memoryStore;
    private readonly SqliteWorkspaceStore _workspaceStore;
    private readonly WorkspaceService _service;

    public WorkspaceCloseRaceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _memoryStore = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService());
        _workspaceStore = new SqliteWorkspaceStore(_factory);
        _service = new WorkspaceService(_memoryStore, _workspaceStore, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>The RED case named by the acceptance criteria: exactly one of a concurrent
    /// consolidate/discard pair succeeds, the other raises, and the outbox is consumed once.</summary>
    [Fact]
    public async Task ConcurrentConsolidateAndDiscard_ExactlyOneSucceeds_AndTheOutboxIsConsumedOnce()
    {
        await _workspaceStore.BeginAsync(new Workspace("ws-1", ProjectId), FixedNow,
            TestContext.Current.CancellationToken);
        await _memoryStore.WriteAsync(new MemoryWriteRequest(ProjectId, "durable fact", WorkspaceId: "ws-1"),
            TestContext.Current.CancellationToken);

        Exception? consolidateError = null;
        Exception? discardError = null;
        var consolidateTask = Task.Run(async () =>
        {
            try
            {
                await _service.ConsolidateAsync(ProjectId, "ws-1", ["all"], TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                consolidateError = ex;
            }
        }, TestContext.Current.CancellationToken);
        var discardTask = Task.Run(async () =>
        {
            try
            {
                await _service.DiscardAsync(ProjectId, "ws-1", TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                discardError = ex;
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(consolidateTask, discardTask);

        (consolidateError is null ^ discardError is null).ShouldBeTrue(
            "exactly one of consolidate/discard must succeed");
        (consolidateError as UnknownWorkspaceException ?? discardError as UnknownWorkspaceException).ShouldNotBeNull(
            "the loser must raise UnknownWorkspaceException");

        var remaining = await _memoryStore.ListContextAsync(ProjectId, ContextNaming.WorkspaceContext("ws-1"),
            TestContext.Current.CancellationToken);
        remaining.ShouldBeEmpty("the outbox must be consumed exactly once, regardless of which caller won");

        var projectEntries = await _memoryStore.ListContextAsync(ProjectId, ContextNaming.ProjectContext(ProjectId),
            TestContext.Current.CancellationToken);
        var status = await ReadWorkspaceStatusAsync("ws-1");
        if (consolidateError is null)
        {
            status.ShouldBe("Closed");
            projectEntries.Count.ShouldBe(1, "consolidate won: the fact must have been promoted exactly once");
        }
        else
        {
            status.ShouldBe("Discarded");
            projectEntries.ShouldBeEmpty("discard won: the fact must never have been promoted");
        }
    }

    /// <summary>The same invariant, run several times to give the (nondeterministic) DB scheduler a
    /// chance to let either side win — a flaky ordering-dependent implementation would eventually fail.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ConcurrentConsolidateAndDiscard_RepeatedRuns_AlwaysExactlyOneWinner(int run)
    {
        var workspaceId = $"ws-{run}";
        await _workspaceStore.BeginAsync(new Workspace(workspaceId, ProjectId), FixedNow,
            TestContext.Current.CancellationToken);
        await _memoryStore.WriteAsync(new MemoryWriteRequest(ProjectId, $"fact {run}", WorkspaceId: workspaceId),
            TestContext.Current.CancellationToken);

        var results = await Task.WhenAll(
            SafeRunAsync(() => _service.ConsolidateAsync(ProjectId, workspaceId, ["all"], TestContext.Current.CancellationToken)),
            SafeRunAsync(() => _service.DiscardAsync(ProjectId, workspaceId, TestContext.Current.CancellationToken)));

        results.Count(succeeded => succeeded).ShouldBe(1, "exactly one caller must win the close");
    }

    private static async Task<bool> SafeRunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (UnknownWorkspaceException)
        {
            return false;
        }
    }

    private async Task<string?> ReadWorkspaceStatusAsync(string workspaceId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM workspaces WHERE id = @workspaceId", new { workspaceId });
    }
}
