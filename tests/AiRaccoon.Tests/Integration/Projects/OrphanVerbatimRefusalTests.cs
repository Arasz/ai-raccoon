using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using AiRaccoon.Tests.Unit.Projects;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     ADR-0099 steady state: the gate passes ids through even when migrated — a write under a
///     former alias spelling lands verbatim (registered ids only), while a TRUE typo is REFUSED
///     with zero new projects rows. Both halves are mechanically gated on the P2 repair_requests
///     finished marker — plus the canonical-only storage assert and the read-passthrough row.
///     <para>
///         Honesty ledger (mutation : filter : fixture): skip-gate-fold :
///         --filter AliasWrite_ThreadsTheVerbatimPartition : migrated bank, registered jsaa + loser, loser write;
///         always-legacy-guard : --filter TrueTypo_Refused : same bank, jsaaa write;
///         drop-write-assert : --filter CanonicalOnlyWrite_ReachesStorage : respelled-guid direct
///         store write bypassing the gate; refuse-reads : --filter OrphanRead_Passthrough :
///         unmigrated bank with loser rows, loser-id search.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class OrphanVerbatimRefusalTests : IAsyncLifetime
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private const string Typo = "jsaaa";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("orphan-verbatim-refusal");
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
    ///     The full production choke: the real marker gate flips the real ToolGate's fold and the
    ///     real guard's refusal over one bank. The queue fake only carries the envelope meta.
    /// </summary>
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

    private async Task SeedMigratedWinnerAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await ((IProjectRegistry)_store).RegisterAsync(Winner, null, ct);
        // ADR-0099: the loser is its own registered project now — pass-through writes need it.
        await ((IProjectRegistry)_store).RegisterAsync(Loser, null, ct);
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = (string?)null },
            cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
            new { kind = RepairKinds.ProjectIds, finishedAt = FixedNow.ToUnixTimeSeconds() },
            cancellationToken: ct));
    }

    [RetryFact]
    public async Task AliasWrite_ThreadsTheVerbatimPartition()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedMigratedWinnerAsync();
        var tools = BuildEnforcingTools();

        // Ledger — skip-gate-fold : --filter AliasWrite_ThreadsTheVerbatimPartition : migrated bank, registered jsaa, loser write (ADR-0099: passes through).
        var written = await tools.Write(Loser, "fold this orphan to its winner",
            cancellationToken: ct);

        // ADR-0099: the wire answers in the named context AND the row lands in the verbatim
        // partition — folding needs a one-shot --map repair, never the gate.
        written.Data!.Context.ShouldBe($"project:{Loser}");
        await using var connection = await _factory.OpenBankAsync(ct);
        var stored = await connection.QueryFirstOrDefaultAsync<(string ProjectId, string? ContextLabel)>(
            new CommandDefinition("SELECT project_id AS ProjectId, context_label AS ContextLabel FROM entries WHERE hash = @hash",
                new { hash = written.Data.Hash }, cancellationToken: ct));
        stored.ProjectId.ShouldBe(Loser);
    }

    [RetryFact]
    public async Task TrueTypo_Refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedMigratedWinnerAsync();
        var tools = BuildEnforcingTools();

        // Ledger — always-legacy-guard : --filter TrueTypo_Refused : migrated bank, jsaaa write, projects-row list.
        var ex = await Should.ThrowAsync<UnregisteredProjectException>(() =>
            tools.Write(Typo, "no such project", cancellationToken: ct));

        ex.Message.ShouldContain("is not registered");
        await using var connection = await _factory.OpenBankAsync(ct);
        var projects = (await connection.QueryAsync<string>(
                new CommandDefinition("SELECT id FROM projects", cancellationToken: ct)))
            .ToList();
        projects.OrderBy(id => id).ToList().ShouldBe(new List<string> { Loser, Winner }.OrderBy(id => id).ToList(), "a refused typo must not create a projects row");
    }

    [RetryFact]
    public async Task CanonicalOnlyWrite_ReachesStorage()
    {
        // Bypasses the gate the way a future direct store caller would: a re-spelled guid must
        // fail LOUDLY at the store instead of landing raw beside the canonical partition.
        var ct = TestContext.Current.CancellationToken;
        var canonical = Guid.CreateVersion7().ToString("D");
        await ((IProjectRegistry)_store).RegisterAsync(canonical, null, ct);
        var respelled = $"{{{canonical.ToUpperInvariant()}}}";

        // Ledger — drop-write-assert : --filter CanonicalOnlyWrite_ReachesStorage : respelled-guid direct store write bypassing the gate.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _store.WriteAsync(new MemoryWriteRequest(respelled, "bypass write"), ct));

        await using var connection = await _factory.OpenBankAsync(ct);
        (await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("SELECT count(*) FROM entries", cancellationToken: ct)))
            .ShouldBe(0, "the refused write stored nothing");
    }

    /// <summary>
    ///     No marker: an unmigrated bank holding loser rows. Loser-id reads must succeed and find
    ///     their rows — enforcement must not break reads, before migration or after. Two rows, not
    ///     one (d-427 SHOULD-5): a single-row passthrough cannot tell "reads pass through" from
    ///     "the one row happened to match".
    /// </summary>
    [RetryFact]
    public async Task OrphanRead_Passthrough()
    {
        // No marker: an unmigrated bank holding loser rows. A loser-id read must succeed and find
        // its rows — enforcement must not break reads, before migration or after.
        var ct = TestContext.Current.CancellationToken;
        var tools = BuildEnforcingTools();
        var first = await tools.Write(Loser, "loser row narwhal tusk",
            cancellationToken: ct);
        var second = await tools.Write(Loser, "loser row narwhal horn",
            cancellationToken: ct);
        await _store.EmbedPendingAsync(Loser, null, ct);

        // Ledger — refuse-reads : --filter OrphanRead_Passthrough : unmigrated bank with loser rows, loser-id search.
        var search = await tools.Search(Loser, "narwhal", sessionId: "sess-pint", cancellationToken: ct);

        search.Data!.Results.ShouldContain(r => r.Hash == first.Data!.Hash);
        search.Data!.Results.ShouldContain(r => r.Hash == second.Data!.Hash);
    }
}
