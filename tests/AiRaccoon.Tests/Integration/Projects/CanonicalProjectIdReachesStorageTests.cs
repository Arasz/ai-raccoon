using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
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

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     ADR-0089 decision 2: two spellings of one guid are one project, never two. Register a
///     guidv7 once, then write/list under a re-spelled form (upper-case, braced) — a behavioural
///     check that the canonical id actually reaches storage, not a syntactic check that some local
///     was assigned. <c>ShareTools.ShareExtract</c> and <c>PromotionTools.List</c> are covered
///     directly because §6c of the WP6 plan names them as the two call sites the assignment alone
///     cannot fix.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CanonicalProjectIdReachesStorageTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("canonical-project-id-tests");
    private readonly FakeTimeProvider _clock = new(FixedNow);
    private string _canonical = null!;
    private string _respelled = null!;
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private FakeEmbeddingEndpoint _embeddingEndpoint = null!;

    public async ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), _clock, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

        _canonical = Guid.CreateVersion7().ToString("D");
        _respelled = $"{{{_canonical.ToUpperInvariant()}}}"; // braced, upper-case — a re-spelling ProjectId.TryCanonicalize must fold back down
        await ((IProjectRegistry)_store).RegisterAsync(_canonical, null, TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(_canonical), "full", TestContext.Current.CancellationToken);

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

    private MemoryTools BuildMemoryTools() =>
        new(_store, new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()),
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()), new MemoryWriteService(_store, new FakePromotionQueue()),
            NoOpMeasurementRecorder.Instance, NullLogger<MemoryTools>.Instance);

    [RetryFact]
    public async Task MemoryWrite_UnderARespelledForm_WritesTheCanonicalLowercaseDForm_ToEntries()
    {
        var tools = BuildMemoryTools();

        var written = await tools.Write(_respelled, "note written under a re-spelled guid",
            cancellationToken: TestContext.Current.CancellationToken);

        var storedProjectId = await ReadStoredProjectIdAsync(written.Data!.Hash);
        storedProjectId.ShouldBe(_canonical);
    }

    [RetryFact]
    public async Task WritesUnderTwoSpellingsOfTheSameGuid_LandInOneProjectNotTwo()
    {
        var tools = BuildMemoryTools();

        var first = await tools.Write(_canonical, "written under the canonical spelling",
            cancellationToken: TestContext.Current.CancellationToken);
        var second = await tools.Write(_respelled, "written under the re-spelled form",
            cancellationToken: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var distinctProjectIds = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT DISTINCT project_id FROM entries WHERE hash IN (@h1, @h2)",
                    new { h1 = first.Data!.Hash, h2 = second.Data!.Hash },
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();

        distinctProjectIds.ShouldBe([_canonical]);
    }

    [RetryFact]
    public async Task MemorySearch_WithARespelledId_FindsRowsWrittenUnderTheCanonicalForm_AndTheVecRowIsPartitionedByIt()
    {
        var tools = BuildMemoryTools();
        var written = await tools.Write(_canonical, "narwhals have a single long tusk",
            cancellationToken: TestContext.Current.CancellationToken);

        // Drain first: a vec_entries row exists only once the entry is embedded, and skipping the
        // drain would make the assertion below pass by finding nothing (MemorySchemaVersionTests.cs:238's shape).
        await _store.EmbedPendingAsync(_canonical, null, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var vecRows = (await connection.QueryAsync<(long RowId, string Ctx)>(
                new CommandDefinition(
                    "SELECT v.rowid AS RowId, v.ctx AS Ctx FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE e.hash = @hash",
                    new { hash = written.Data!.Hash },
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();

        vecRows.ShouldNotBeEmpty("the drain must have embedded the row before this assertion runs");
        vecRows.ShouldAllBe(r =>
            r.Ctx == MemorySql.ContextKeyFor(ContextNaming.ProjectContext(_canonical), _canonical));

        var search = await tools.Search(_respelled, "narwhal tusk", cancellationToken: TestContext.Current.CancellationToken);

        search.Data!.Results.ShouldContain(r => r.Hash == written.Data!.Hash);
    }

    [RetryFact]
    public async Task MemoryShareExtract_UnderARespelledForm_ThreadsTheCanonicalId_ToTheExtractionRunner()
    {
        var extraction = new RecordingExtractionRunner();
        var gate = new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard());
        var tools = new ShareTools(_store, gate, new ShareExtractService(_store, extraction, new FakePromotionQueue()));

        await tools.ShareExtract([_respelled], cancellationToken: TestContext.Current.CancellationToken);

        extraction.LastProjectId.ShouldBe(_canonical,
            "ShareTools.ShareExtract rebuilds the request with the canonical ids collected in its loop (ShareTools.cs)");
    }

    [RetryFact]
    public async Task MemoryPromotionList_UnderARespelledForm_ListsUnderTheCanonicalId()
    {
        var queue = new FakePromotionQueue();
        var gate = new ToolGate(new MemoryAccessGuard(_store), queue, new NeverMigratingStore(), new AllowingRegistrationGuard());
        var tools = new PromotionTools(queue, gate);

        await tools.List(_respelled, cancellationToken: TestContext.Current.CancellationToken);

        queue.LastListProject.ShouldBe(_canonical,
            "PromotionTools.List declares its canonical local before the projectId-is-not-null branch (PromotionTools.cs)");
    }

    private async Task<string?> ReadStoredProjectIdAsync(string hash)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QueryFirstOrDefaultAsync<string>(
            new CommandDefinition("SELECT project_id FROM entries WHERE hash = @hash",
                new { hash }, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Records the projectId ISharedExtractionRunner.ProposeAsync was actually called with — the
    /// production seam ShareExtractService drives per project id in request.ProjectIds.</summary>
    private sealed class RecordingExtractionRunner : ISharedExtractionRunner
    {
        public string? LastProjectId { get; private set; }

        public Task<IReadOnlyList<ShareCandidate>> ProposeAsync(string projectId, SharedIndex sharedIndex,
            bool includeTtlRows, int limit, double? minScore = null, CancellationToken cancellationToken = default)
        {
            LastProjectId = projectId;
            return Task.FromResult<IReadOnlyList<ShareCandidate>>([]);
        }
    }
}
