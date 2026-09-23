using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     memory_search end to end over a real bank and the bundled embedding model: a row the keyword
///     leg matched on every query term is an answer even when its content cosine sits below the
///     absolute relevance floor, while a query that shares no content term with the bank still
///     comes back empty.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class KeywordMatchRelevanceFloorTests : IAsyncLifetime
{
    private const string ProjectId = "acme";
    private const string Session = "sess-test";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-keyword-floor-tests");
    private SqliteMemoryStore _store = null!;
    private MemoryTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await TestData.CreateBundledModel().EnsureAsync(ct);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var clock = new FakeTimeProvider(FixedNow);
        var embeddings = TestData.CreateEmbeddingService();
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory),
            TestData.RealMarkdownChunker(), clock, embeddings, null, null, null, null, null, null, null);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, factory, embeddings, "local", null, null, ct, clock);

        var settings = new InMemorySettings();
        var gate = new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(),
            new AllowingRegistrationGuard(), new NeverMigratedGate());
        _tools = new MemoryTools(_store, gate,
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(settings), new MemoryWriteService(_store, new FakePromotionQueue()),
            new NoOpMeasurementRecorder(), settings, NullLogger<MemoryTools>.Instance, new CountingEmbeddingService());
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     The reported defect: an entry containing "AIR-4471" verbatim, found first by the keyword
    ///     leg, was dropped by the cosine floor and the default call returned nothing.
    /// </summary>
    [RetryFact]
    public async Task Search_TicketIdentifierMatchedByKeyword_IsServedByTheDefaultCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticket = await SeedProseBankAsync(ct);

        var recall = await _tools.Search(ProjectId, "AIR-4471", Session, kind: "memory", minRelativeScore: 0.0, cancellationToken: ct);
        var evidence = recall.Data!.EvidenceByHash.ShouldNotBeNull()[ticket.Hash];
        evidence.Cosine.ShouldNotBeNull().ShouldBeLessThan(SearchRelevance.AbsoluteRelevanceFloor,
            "premise: the identifier row's content cosine sits under the absolute floor");

        var envelope = await _tools.Search(ProjectId, "AIR-4471", Session, kind: "memory", cancellationToken: ct);

        envelope.Data!.Results.Select(result => result.Hash).ShouldContain(ticket.Hash,
            "a row containing every query term is an answer, whatever its cosine");
        envelope.Data!.Results[0].Hash.ShouldBe(ticket.Hash);
    }

    /// <summary>The floor's own measured case: no content term in common, so nothing is served.</summary>
    [RetryFact]
    public async Task Search_ZeroOverlapQuery_StillComesBackEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedProseBankAsync(ct);

        var envelope = await _tools.Search(ProjectId, "quantum chromodynamics lattice gauge", Session, kind: "memory", cancellationToken: ct);

        envelope.Data!.Results.ShouldBeEmpty();
    }

    /// <summary>
    ///     The keyword leg's OR fallback keeps stop words, so an off-corpus question still gets FTS
    ///     hits on "to"/"a"/"in". Those rows matched no content term and must stay under the floor.
    /// </summary>
    [RetryFact]
    public async Task Search_OffCorpusQuestionMatchingOnlyStopWords_StillComesBackEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedProseBankAsync(ct);

        var recall = await _tools.Search(ProjectId, "how to braise a wombat in aspic", Session, kind: "memory",
            minRelativeScore: 0.0, cancellationToken: ct);
        recall.Data!.EvidenceByHash.ShouldNotBeNull().Values
            .ShouldContain(item => item.Legs.Any(leg => leg.LegName == "fts"),
                "premise: the stop-word fallback gives the keyword leg hits");

        var envelope = await _tools.Search(ProjectId, "how to braise a wombat in aspic", Session, kind: "memory", cancellationToken: ct);

        envelope.Data!.Results.ShouldBeEmpty();
    }

    private async Task<MemoryEntry> SeedProseBankAsync(CancellationToken ct)
    {
        var ticket = await _store.WriteAsync(new MemoryWriteRequest(ProjectId,
            "Deploy blocked on AIR-4471 until the staging certificate is rotated in the vault."), ct);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId,
            "The harbor ledger tracks anchovy shipments that arrive in the morning."), ct);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId,
            "A lighthouse keeper walks to the lamp room in the evening to polish a brass lens."), ct);
        await _store.WriteAsync(new MemoryWriteRequest(ProjectId,
            "Sourdough starter needs a warm kitchen and flour fed to it in equal weight."), ct);
        return ticket;
    }
}
