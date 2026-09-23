using AiRaccoon.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     memory_search ranking end to end over a real bank and the bundled embedding model: which rows
///     survive the absolute relevance floor, which row a file#section anchor puts first, and what
///     the same-source boost may not overtake.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySearchRankingTests : IAsyncLifetime
{
    private const string ProjectId = "acme";
    private const string Session = "sess-test";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-search-ranking-tests");
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private MemoryTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await TestData.CreateBundledModel().EnsureAsync(ct);
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var clock = new FakeTimeProvider(FixedNow);
        var embeddings = TestData.CreateEmbeddingService();
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory),
            TestData.RealMarkdownChunker(), clock, embeddings, null, null, null, null, null, null, null);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, factory, embeddings, "local", null, null, ct, clock);

        var settings = new SqliteSettingsStore(factory);
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

    /// <summary>
    ///     A file#section anchor names one section, so its rows lead the response whatever the vector
    ///     leg makes of a path string. Here the section is freshly ingested and not yet embedded, so
    ///     it scores keyword rank 1 only and ties the vector leg's rank-1 row from another file.
    /// </summary>
    [RetryTheory]
    [InlineData(SearchDefaults.MinRelativeScore)]
    [InlineData(0.0)]
    public async Task Search_FileSectionAnchor_RanksTheNamedSectionFirst(double minRelativeScore)
    {
        var ct = TestContext.Current.CancellationToken;
        await AllowIngestAsync(ct);
        await IngestAsync("a-deploy-notes.md",
            "# Deploy notes\n\nThe deploy runbook rollback section explains how to roll a deploy back.\n", ct);
        await _store.EmbedPendingAsync(ProjectId, null, ct);
        var runbook = await IngestAsync("deploy-runbook.md", RunbookText(), ct);

        var envelope = await _tools.Search(ProjectId, $"{runbook}#rollback", Session, kind: "memory",
            minRelativeScore: minRelativeScore, cancellationToken: ct);

        envelope.Data!.Results.ShouldNotBeEmpty("the anchored section is an answer, not noise under a cosine floor");
        var top = envelope.Data!.Results[0];
        top.SourceFile.ShouldBe(runbook);
        (await SectionOfAsync(top.Hash, ct)).ShouldBe("Rollback", StringCompareShould.IgnoreCase);
    }

    /// <summary>
    ///     An embedded section's content cosine to a path string is near zero, which is no evidence
    ///     either way: the default call still serves the named section, first.
    /// </summary>
    [RetryFact]
    public async Task Search_FileSectionAnchor_EmbeddedSection_IsServedFirstByTheDefaultCall()
    {
        var ct = TestContext.Current.CancellationToken;
        await AllowIngestAsync(ct);
        var runbook = await IngestAsync("deploy-runbook.md", RunbookText(), ct);
        await _store.EmbedPendingAsync(ProjectId, null, ct);

        var envelope = await _tools.Search(ProjectId, $"{runbook}#rollback", Session, kind: "memory", cancellationToken: ct);

        envelope.Data!.Results.ShouldNotBeEmpty();
        (await SectionOfAsync(envelope.Data!.Results[0].Hash, ct)).ShouldBe("Rollback", StringCompareShould.IgnoreCase);
    }

    /// <summary>
    ///     Both legs rank one note first; a runbook's weak adjacent chunks trail it. The adjacent-chunk
    ///     boost must not lift those neighbours above the row both legs agree on, even with every
    ///     score cutoff off.
    /// </summary>
    [RetryFact]
    public async Task Search_RowBothLegsRankFirst_StaysFirstAboveBoostedNeighbours()
    {
        var ct = TestContext.Current.CancellationToken;
        var note = await _store.WriteAsync(new MemoryWriteRequest(ProjectId,
            "Rotate the staging certificate before it expires: certificate rotation runs from the vault."), ct);
        await AllowIngestAsync(ct);
        await IngestAsync("deploy-runbook.md", RunbookText(), ct);
        await _store.EmbedPendingAsync(ProjectId, null, ct);

        var envelope = await _tools.Search(ProjectId, "staging certificate rotation", Session, kind: "memory",
            minRelativeScore: 0.0, cancellationToken: ct);

        var legs = envelope.Data!.EvidenceByHash.ShouldNotBeNull()[note.Hash].Legs;
        legs.ShouldContain(leg => leg.LegName == "fts" && leg.Rank == 1, "premise: the keyword leg ranks the note first");
        legs.ShouldContain(leg => leg.LegName == "vector" && leg.Rank == 1, "premise: the vector leg ranks the note first");
        envelope.Data!.Results[0].Hash.ShouldBe(note.Hash, "a same-source boost must not lift weaker neighbours above it");
    }

    private static string RunbookText()
    {
        var sections = new[]
        {
            ("Overview", "This runbook describes how the platform team ships the web tier."),
            ("Preparation", "Before a deploy the on-call engineer checks the change calendar and warms the build cache."),
            ("Rollback", "Kittens nap on warm windowsills while gardeners water tomatoes and bees drift between blossoms."),
            ("Verification", "After the deploy the team watches dashboards and confirms synthetic probes stay green.")
        };
        return "# Deploy runbook\n\n" + string.Join("\n\n", sections.Select(section =>
            $"## {section.Item1}\n\n{string.Join(" ", Enumerable.Repeat(section.Item2, 12))}")) + "\n";
    }

    private Task AllowIngestAsync(CancellationToken ct) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject(ProjectId), IngestScopeKeys.Serialize([_dataRoot]), ct);

    private async Task<string> IngestAsync(string name, string content, CancellationToken ct)
    {
        var file = Path.Combine(_dataRoot, name);
        await File.WriteAllTextAsync(file, content, ct);
        (await _store.IngestFileAsync(ProjectId, file, null, ct)).ShouldBe(1);
        return file;
    }

    private async Task<string?> SectionOfAsync(string hash, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition("SELECT section FROM entries WHERE hash = @hash", new { hash }, cancellationToken: ct));
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
