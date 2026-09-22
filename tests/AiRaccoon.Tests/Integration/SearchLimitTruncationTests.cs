using System.Text.Json;
using AiRaccoon.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Storage;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     P3.2 (F20) end to end on the measured scenario shape: a bank whose candidate population is
///     far larger than the default relative floor lets through. limit=100 used to return 41 of 70
///     with nothing on the wire to tell "the floor cut this" from "the bank holds 41 matches".
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchLimitTruncationTests : IDisposable
{
    private const string ProjectId = "proj-1";

    // 70 candidates at the shipped k=60: the merge's ranking is (k+1)/(k+rank), so the default
    // relative floor 0.6 keeps exactly ranks 1..41 (61/101 = 0.604 >= 0.6, 61/102 = 0.598 < 0.6).
    private const int SeedCount = 70;
    private const int FloorSurvivors = 41;
    private const int DroppedByFloor = SeedCount - FloorSurvivors;

    private readonly string _dataRoot;
    private readonly SqliteMemoryStore _store;

    public SearchLimitTruncationTests()
    {
        _dataRoot = TestData.CreateTempRoot("ai-raccoon-limit-truncation");
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        ISqliteConnectionFactory factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = SearchTimingsHarness.CreateStore(factory,
            new FakeTimeProvider(new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero)),
            new SearchTimingsHarness.VectorEmbedderStub());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>The measured scenario: limit=100 on a 70-candidate bank must not answer "41" and say nothing.</summary>
    [RetryFact]
    public async Task RaisedLimit_ShortByTheDefaultRelativeFloor_ReportsTruncationNamingTheFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tools = BuildTools(_store);

        var envelope = await tools.Search(ProjectId, "widgets", kind: "memory", limit: 100,
            sessionId: "sess-test", cancellationToken: ct);

        envelope.Data!.Results.Count.ShouldBe(FloorSurvivors,
            "the default relative floor keeps ranks 1..41 of the 70 fused candidates");
        var json = JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions);
        json.ShouldContain("\"truncation\"");
        json.ShouldContain("\"floor\":\"minRelativeScore\"");
        json.ShouldContain($"\"dropped\":{DroppedByFloor}");
    }

    /// <summary>
    ///     Positive control: the full-recall hatch (minRelativeScore=0) serves every candidate and
    ///     marks no truncation — the marker reports floor cuts, not short banks.
    /// </summary>
    [RetryFact]
    public async Task ExplicitZeroFloor_ServesEveryCandidate_WithNoTruncationMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tools = BuildTools(_store);

        var envelope = await tools.Search(ProjectId, "widgets", kind: "memory", limit: 100,
            minRelativeScore: 0.0, sessionId: "sess-test", cancellationToken: ct);

        envelope.Data!.Results.Count.ShouldBe(SeedCount, "no score floor is active at minRelativeScore 0");
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("truncation");
    }

    /// <summary>
    ///     Anti-noise control: a response that fills the requested limit is complete whatever the
    ///     floors did to the candidate tail, and carries no marker.
    /// </summary>
    [RetryFact]
    public async Task DefaultLimit_CompleteResponseCarriesNoTruncationMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tools = BuildTools(_store);

        var envelope = await tools.Search(ProjectId, "widgets", kind: "memory",
            sessionId: "sess-test", cancellationToken: ct);

        envelope.Data!.Results.Count.ShouldBe(8, "the default limit is filled");
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("truncation");
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < SeedCount; index++)
        {
            await _store.WriteAsync(new MemoryWriteRequest(ProjectId,
                $"widgets ledger entry {index} with distinct filler words number {index}"), cancellationToken);
        }
    }

    private static MemoryTools BuildTools(SqliteMemoryStore store)
    {
        var gate = new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue(),
            new NeverMigratingStore(), new AllowingRegistrationGuard(), migrationGate: new StubMigrationGate(migrated: false));
        return new MemoryTools(store, gate,
            new SearchDispatcher(store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(store, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(),
            NullLogger<MemoryTools>.Instance);
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }
}
