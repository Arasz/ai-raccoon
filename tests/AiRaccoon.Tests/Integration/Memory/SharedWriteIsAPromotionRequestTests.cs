using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
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

    public SharedWriteIsAPromotionRequestTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService());
        // A recording queue, not the real graph: this gate is about what the WRITE path decides.
        // That a proposed candidate persists is PromotionQueueService's own contract and its tests.
        _writes = new MemoryWriteService(store, _queue);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The boundary: a `shared` write must not create a shared row. Before the fix it created one
    ///     and no queue row, so the request crossed the project boundary AND was never reviewed.
    /// </summary>
    [Fact]
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
    [Fact]
    public async Task OrdinaryWrite_QueuesNothing()
    {
        await _writes.WriteAsync(new MemoryWriteRequest(ProjectId, Content),
            TestContext.Current.CancellationToken);

        _queue.LastCandidates.ShouldBeNull("only an explicit `shared` write is a promotion request");
    }
}
