using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Air-merge P-INT end-to-end: the full production choke (real <see cref="ProjectIdsRepairJob" />
///     → real migration-gate marker → real <see cref="ToolGate" /> fold → real guard refusal) over
///     one repaired bank, split in three so each leg carries its own mutation. P3's
///     OrphanVerbatimRefusalTests hand-stamps the marker; here the marker is earned by the job —
///     that job-to-wire handoff is the integration subject.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SingleProjectIdE2E : IAsyncLifetime
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private const string Typo = "jsaaa";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("single-project-id-e2e");
    private readonly FakeTimeProvider _clock = new(FixedNow);
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private FakeEmbeddingEndpoint _embeddingEndpoint = null!;

    public async ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), _clock, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

        _embeddingEndpoint = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _factory, TestData.CreateEmbeddingService(),
            "openai", "nomic-embed-text", _embeddingEndpoint.BaseUrl, TestContext.Current.CancellationToken, _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _embeddingEndpoint.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     After a real repair, a true typo is refused on the wire with the published
    ///     project-not-registered code and registers nothing as a side effect.
    ///     Ledger — restore-auto-register-branch : --filter "FullyQualifiedName~SingleProjectIdE2E.TypoRefusedOnWire" :
    ///     repaired jsaa bank (winner + folded loser rows), jsaaa write.
    /// </summary>

    private static string FixtureMapJson() =>
        new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
            ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
            ["qa-noise-project", "manual-sweep"]).ToJson();

    [RetryFact]
    public async Task TypoRefusedOnWire()
    {
        var ct = TestContext.Current.CancellationToken;
        var tools = await SeedAndRepairAsync(winnerContent: "winner quokka ledger", loserContent: "loser wombat ledger", ct);

        var ex = await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.Write(Typo, "no such project", cancellationToken: ct));

        ToolRefusals.PrefixFor(ex).ShouldBe("project-not-registered",
            "the .NET refusal maps to the published wire code — the contract clients bind to");
        await using var connection = await _factory.OpenBankAsync(ct);
        var projects = (await connection.QueryAsync<string>(
                new CommandDefinition("SELECT id FROM projects", cancellationToken: ct)))
            .ToList();
        projects.ShouldBe([Winner], "a refused typo must not create a projects row");
    }

    /// <summary>
    ///     Content written under both spellings before the repair is searchable under the single
    ///     winner afterwards — with the D1 boundary pinned at the wire: labeled AND NULL-context bulk
    ///     loser rows fold and read under the winner (the d-426 keep is overturned: no consumer keys on
    ///     (project_id, NULL-label) stability). Reads never refuse and scopes pass through (ADR-0099).
    ///     Property intersection: labeled × unlabeled storage against winner-scoped × loser-scoped reads.
    ///     Ledger — revert-jsaa-fold : --filter "FullyQualifiedName~SingleProjectIdE2E.MergedClusterSearch" :
    ///     labeled loser wombat row (SQL) + NULL-ctx loser quokka row (wire) + winner capybara row,
    ///     real repair, embed drain of BOTH partitions (d-427 SHOULD-4: the neither-served asserts run
    ///     against embedded state), winner/loser-scoped searches.
    /// </summary>
    [RetryFact]
    public async Task MergedClusterSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        var tools = BuildEnforcingTools();
        var winnerWritten = await tools.Write(Winner, "winner capybara arbiter", cancellationToken: ct);
        var bulkWritten = await tools.Write(Loser, "loser quokka zelinsky", cancellationToken: ct);
        await using (var seed = await _factory.OpenBankAsync(ct))
        {
            await seed.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES ('labeled-1', 'labeled-1', 'loser wombat platonov', 'seed.md', 's', 'project', @loser, 'ctx-a', 1, 1, 'pending')",
                new { loser = Loser }, cancellationToken: ct));
            await seed.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() },
                cancellationToken: ct));
            (await NewRepairJob().RunAsync(seed, ct)).ShouldBeTrue("the labeled loser row must fold");
        }

        await _store.EmbedPendingAsync(Winner, null, ct);
        // d-427 SHOULD-4: drain BOTH partitions, not just the winner's — the neither-served
        // asserts below must hold against fully-embedded state, or the vec leg passes vacuously
        // (an unembedded row is invisible for the wrong reason).
        await _store.EmbedPendingAsync(Loser, null, ct);
        var foldedHits = await tools.Search(Winner, "wombat platonov", sessionId: "sess-pint", cancellationToken: ct);
        var winnerHits = await tools.Search(Winner, "capybara arbiter", sessionId: "sess-pint", cancellationToken: ct);
        var bulkUnderWinner = await tools.Search(Winner, "quokka zelinsky", sessionId: "sess-pint", cancellationToken: ct);
        var bulkUnderLoser = await tools.Search(Loser, "quokka zelinsky", sessionId: "sess-pint", cancellationToken: ct);

        foldedHits.Data!.Results.ShouldContain(r => r.Hash == "labeled-1",
            "the labeled loser content folded under the winner");
        winnerHits.Data!.Results.ShouldContain(r => r.Hash == winnerWritten.Data!.Hash,
            "the winner's own content stays searchable");
        bulkUnderWinner.Data!.Results.ShouldContain(r => r.Hash == bulkWritten.Data!.Hash,
            "D1: NULL-context bulk rows fold — the winner scope serves the folded row");
        bulkUnderLoser.Data!.Results.ShouldBeEmpty(
            "the loser key owns nothing after the fold, so its scope serves nothing");
        await using var connection = await _factory.OpenBankAsync(ct);
        (await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM entries WHERE project_id = @loser AND context_label IS NOT NULL",
                    new { loser = Loser }, cancellationToken: ct)))
            .ShouldBe(0, "no labeled loser row survives to split a reader's view");
        (await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM entries WHERE project_id = @loser",
                    new { loser = Loser }, cancellationToken: ct)))
            .ShouldBe(0, "the bulk row folds with the labeled rows — zero loser rows remain");
    }

    /// <summary>
    ///     The split queue meets under the winner in storage; the wire passes ids through
    ///     (ADR-0099): each spelling lists its own queue at the gate. The P4 boundary fold
    ///     observed through the MCP list tool on a repaired bank.
    ///     Ledger — revert-jsaa-fold : --filter "FullyQualifiedName~SingleProjectIdE2E.QueueMeetsOnWire" :
    ///     winner + loser queue rows (2 × 2), real repair, wire List under both spellings.
    /// </summary>
    [RetryFact]
    public async Task QueueMeetsOnWire()
    {
        var ct = TestContext.Current.CancellationToken;
        var tools = await SeedAndRepairAsync(winnerContent: "winner quokka queue", loserContent: "loser wombat queue", ct);
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) VALUES " +
                "('jsaa', 'q-w1', 'q-w1', 0.9, 10, 12), ('jsaa', 'q-w2', 'q-w2', 0.8, 10, 12), " +
                "('job-search-ai-assistant', 'q-l1', 'q-l1', 0.7, 9, 11), ('job-search-ai-assistant', 'q-l2', 'q-l2', 0.6, 9, 11)",
                cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() },
                cancellationToken: ct));
            await NewRepairJob().RunAsync(connection, ct);
        }

        var queue = new FakePromotionQueue();
        var promotion = PromotionToolsFor(queue);
        await promotion.List(Winner, cancellationToken: ct);
        queue.LastListProject.ShouldBe(Winner);
        await promotion.List(Loser, cancellationToken: ct);

        queue.LastListProject.ShouldBe(Loser, "ADR-0099: the gate passes ids through — each spelling lists its own queue");
        await using var verify = await _factory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM promotion_queue WHERE project_id = @winner",
                    new { winner = Winner }, cancellationToken: ct)))
            .ShouldBe(4, "every queued row meets under the winner: 2 winner + 2 folded loser");
        (await verify.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM promotion_queue WHERE project_id = @loser",
                    new { loser = Loser }, cancellationToken: ct)))
            .ShouldBe(0, "the loser queue key is absent after the fold");
    }

    /// <summary>
    ///     Writes under both spellings on the unmigrated bank (legacy first-write auto-register),
    ///     then earns the P3 marker by running the real repair job — the handoff under test.
    /// </summary>
    private async Task<MemoryTools> SeedAndRepairAsync(string winnerContent, string loserContent, CancellationToken ct)
    {
        var tools = BuildEnforcingTools();
        await tools.Write(Winner, winnerContent, cancellationToken: ct);
        await tools.Write(Loser, loserContent, cancellationToken: ct);

        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = FixtureMapJson() },
                cancellationToken: ct));
            (await NewRepairJob().RunAsync(connection, ct)).ShouldBeTrue("the seeded split bank must fold");
        }

        (await new SqliteProjectIdsMigrationGate(_factory).IsMigratedAsync(ct))
            .ShouldBeTrue("the job stamps the marker the gate enforces on");
        return BuildEnforcingTools();
    }

    private MemoryTools BuildEnforcingTools()
    {
        var marker = new SqliteProjectIdsMigrationGate(_factory);
        var guard = new ProjectRegistrationGuard(_store, NullLogger<ProjectRegistrationGuard>.Instance, marker);
        var gate = new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(),
            new NeverMigratingStore(), guard, migrationGate: marker);
        return new MemoryTools(_store, gate,
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()), new MemoryWriteService(_store, new FakePromotionQueue()),
            NoOpMeasurementRecorder.Instance, NullLogger<MemoryTools>.Instance);
    }

    private PromotionTools PromotionToolsFor(FakePromotionQueue queue)
    {
        var marker = new SqliteProjectIdsMigrationGate(_factory);
        var guard = new ProjectRegistrationGuard(_store, NullLogger<ProjectRegistrationGuard>.Instance, marker);
        var gate = new ToolGate(new MemoryAccessGuard(_store), queue,
            new NeverMigratingStore(), guard, migrationGate: marker);
        return new PromotionTools(queue, gate);
    }

    private ProjectIdsRepairJob NewRepairJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));
}
