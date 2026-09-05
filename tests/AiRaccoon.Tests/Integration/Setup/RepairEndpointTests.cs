using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     ADR-0075 amendment: `repair` reaches the server entirely, both the read-only report and the
///     apply request — the CLI never opens the bank for it. Modelled on <see cref="SettingsEndpointTests" />.
/// </summary>
/// <remarks>
///     The request row is counted without the finished_at filter: the maintenance loop's 15s
///     on-demand poll can legitimately apply the request between the POST and the count, so the
///     endpoint's contract is the commit, not the row staying open.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RepairEndpointTests : IAsyncLifetime
{
    private const string Token = "repair-endpoint-token";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-repair-endpoint");
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _client.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [RetryFact]
    public async Task GetReingestReport_OnAnUnaffectedBank_ReportsNothingToDo()
    {
        var response = await _client.GetAsync("/repair?kind=reingest", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ReingestRepairReport>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().FilesToReingest.ShouldBe(0);
    }

    [RetryFact]
    public async Task GetChunkIndexReport_OnAnUnaffectedBank_ReportsNothingToDo()
    {
        var response = await _client.GetAsync("/repair?kind=chunk-index", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ChunkIndexRepairReport>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().RowsRepositioned.ShouldBe(0);
    }

    [RetryTheory]
    [InlineData("/repair")]
    [InlineData("/repair?kind=unknown")]
    public async Task Get_WithNoOrAnUnknownKind_IsABadRequest(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RetryFact]
    public async Task PostReingest_CommitsARequestRow()
    {
        var response = await _client.PostAsJsonAsync("/repair", new RepairRequest("reingest"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RequestCountAsync("reingest")).ShouldBe(1);
    }

    [RetryFact]
    public async Task Post_WithAnUnknownKind_IsABadRequest()
    {
        var response = await _client.PostAsJsonAsync("/repair", new RepairRequest("unknown"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RetryFact]
    public async Task WithoutTheToken_EveryVerbIsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync("/repair?kind=reingest", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsJsonAsync("/repair", new RepairRequest("reingest"), TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    ///     P2 diagnose gate (review MUST-2/MUST-4): the ONLY test that carries the gate — the CLI
    ///     ProjectIds_* rows assert dispatcher/formatter output, never this. Renamed to the gate name
    ///     so the narrowest --filter hits it (a --filter miss exits 0).
    ///     Ledger — comment-out-the-jsaa-cluster-branch : --filter Diagnose_ListsJsaaCluster : ≥2 loser
    ///     entries + both queue legs (one-sided queue passes while dropping a side, review MUST-3).
    /// </summary>
    [RetryFact]
    public async Task Diagnose_ListsJsaaCluster()
    {
        await SeedProjectIdsAsync();

        var response = await _client.GetAsync("/repair?kind=project-ids", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<ProjectIdCensusReport>(TestContext.Current.CancellationToken);
        report.ShouldNotBeNull();
        report.Row("jsaa").ProjectEntries.ShouldBe(2);
        report.Row("jsaa").Queued.ShouldBe(1, "the winner queue leg must be asserted too — one-sided passes while dropping a side");
        report.Row("job-search-ai-assistant").ProjectEntries.ShouldBe(2);
        report.Row("job-search-ai-assistant").Queued.ShouldBe(1);
    }

    /// <summary>
    ///     --apply commits the outbox row (the server drains it). Ledger — drop-TryParseKind-ProjectIds-arm :
    ///     --filter PostProjectIds_CommitsARequestRow : live server bank, no seed rows needed.
    /// </summary>
    [RetryFact]
    public async Task PostProjectIds_CommitsARequestRow()
    {
        var response = await _client.PostAsJsonAsync("/repair", new RepairRequest("project-ids"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RequestCountAsync("project-ids")).ShouldBe(1);
    }

    /// <summary>ADR-0099: a valid one-shot map rides the request row; a malformed one is refused before anything is stored.</summary>
    [RetryFact]
    public async Task PostProjectIds_WithAMapJson_StashesItOnTheRow()
    {
        var mapJson = new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("old-id", "new-id")], ["new-id"], []).ToJson();
        var response = await _client.PostAsJsonAsync("/repair", new RepairRequest("project-ids", mapJson),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MapJsonAsync("project-ids")).ShouldBe(mapJson);
    }

    [RetryFact]
    public async Task PostProjectIds_WithMalformedMapJson_IsABadRequest()
    {
        var before = await RequestCountAsync("project-ids");
        var response = await _client.PostAsJsonAsync("/repair", new RepairRequest("project-ids", "{ not json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RequestCountAsync("project-ids")).ShouldBe(before);
    }

    /// <summary>
    ///     WP3/T6: the write-lock-timeout regression proven end-to-end, not just at the store level
    ///     (SqliteRepairStoreTests' T3 owns that proof). A legacy watch.scope.* row would otherwise
    ///     force the read-only report's own bank open through the full migration ladder, which
    ///     blocks on another connection's BEGIN IMMEDIATE — a read-only GET must never be able to
    ///     fail or stall that way.
    /// </summary>
    [RetryFact]
    public async Task GetProjectIdsReport_WhileAWriterHoldsTheBankLock_Answers200()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedProjectIdsAsync();

        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var writerConnection = await factory.OpenBankAsync(ct);
        var (writer, releaseWriter) = await HoldWriteLockOverLegacyScopeRowAsync(writerConnection, ct);

        var stopwatch = Stopwatch.StartNew();
        var response = await _client.GetAsync("/repair?kind=project-ids", ct);
        stopwatch.Stop();

        releaseWriter.TrySetResult(true);
        await writer;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<ProjectIdCensusReport>(ct);
        report.ShouldNotBeNull();
        report.Row("jsaa").ProjectEntries.ShouldBe(2);
        report.Row("job-search-ai-assistant").ProjectEntries.ShouldBe(2);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2),
            "a read-only report must not wait on the bank's write lock");
    }

    /// <summary>
    ///     WP3/T7: the materialized-CTE vec0 fix (ac72f4f8) survives real HTTP + Dapper mapping +
    ///     endpoint serialization once planner statistics exist, mirroring
    ///     ProjectIdCensusTests' seeded-vec-legs shape (T2) and its planted sqlite_stat1 recipe (T1).
    ///     The plan assertion itself stays owned by T1 — this only checks the numbers the real
    ///     pipeline hands back: alpha and beta each own one content- and one structure-embedded row;
    ///     the NULL-project row and the vec-less gamma row contribute to neither.
    /// </summary>
    [RetryFact]
    public async Task GetProjectIdsReport_OnAStatisticsBearingBank_ReturnsTheSameCensus()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using (var connection = await factory.OpenBankAsync(ct))
        {
            await SeedVecLegsAsync(connection, ct);

            // Order is load-bearing: a later plain ANALYZE recomputes real stats and silently
            // overwrites the planted row, making this test vacuously pass.
            // commandType is forced to Text: Dapper otherwise treats a bare single-identifier
            // command text (e.g. "ANALYZE") as a stored-procedure name.
            await connection.ExecuteAsync(new CommandDefinition(
                "ANALYZE", commandType: CommandType.Text, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM sqlite_stat1 WHERE tbl = 'entries'", cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO sqlite_stat1(tbl, idx, stat) VALUES ('entries','idx_entries_embed_state','48257 48257 3017')",
                cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "ANALYZE sqlite_master", commandType: CommandType.Text, cancellationToken: ct));
        }

        var response = await _client.GetAsync("/repair?kind=project-ids", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<ProjectIdCensusReport>(ct);
        report.ShouldNotBeNull();
        report.Row("alpha").VecEntryRows.ShouldBe(1);
        report.Row("alpha").VecStructureRows.ShouldBe(1);
        report.Row("beta").VecEntryRows.ShouldBe(1);
        report.Row("beta").VecStructureRows.ShouldBe(1);
        report.Row("gamma").VecEntryRows.ShouldBe(0, "gamma has an entries row but no vec_entries row at all");
        report.Row("gamma").VecStructureRows.ShouldBe(0, "gamma has an entries row but no vec_structure row at all");
        report.Rows.ShouldNotContain(r => string.IsNullOrEmpty(r.ProjectId),
            "the project_id-NULL embedded row must never surface as a row of its own");
    }

    /// <summary>
    ///     Seeds a legacy watch.scope.* row through <paramref name="writerConnection" />, then holds
    ///     BEGIN IMMEDIATE open on it until the returned <see cref="TaskCompletionSource{TResult}" />
    ///     is completed — mirrors SqliteRepairStoreTests' HoldWriteLockOverLegacyScopeRowAsync, at
    ///     the HTTP level.
    /// </summary>
    private static async Task<(Task Writer, TaskCompletionSource<bool> Release)> HoldWriteLockOverLegacyScopeRowAsync(
        SqliteConnection writerConnection, CancellationToken cancellationToken)
    {
        await writerConnection.ExecuteAsync("INSERT INTO settings (key, value) VALUES ('watch.scope.jsaa', '[]')");

        var lockHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await writerConnection.ExecuteAsync("BEGIN IMMEDIATE");
            try
            {
                await writerConnection.ExecuteAsync("UPDATE settings SET value = value WHERE key = 'watch.scope.jsaa'");
                lockHeld.TrySetResult(true);
                await releaseWriter.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            finally
            {
                await writerConnection.ExecuteAsync("COMMIT");
            }
        }, cancellationToken);
        await lockHeld.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

        return (writer, releaseWriter);
    }

    /// <summary>
    ///     T7's vec0 seed (mirrors ProjectIdCensusTests.SeedVecLegsAsync): two ids each own one
    ///     content-embedded and one structure-embedded row (kept on separate entries so the legs
    ///     cannot cross-contaminate), a project_id-NULL embedded row, and a vec-less entry.
    /// </summary>
    private static async Task SeedVecLegsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var vector = EmbeddingBlob.ToBytes(new float[384]);

        async Task<long> InsertEntry(string hash, string? projectId, string scope = "project")
        {
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @hash, 'seed.md', 's', @scope, @projectId, 'ctx', @now, @now, 'pending') RETURNING id",
                new { hash, scope, projectId, now }, cancellationToken: cancellationToken));
        }

        async Task EmbedContent(long id)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET embedding = @embedding, embed_state = 'embedded' WHERE id = @id",
                new { embedding = vector, id }, cancellationToken: cancellationToken));
        }

        async Task EmbedStructure(long id)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET structure_embedding = @embedding WHERE id = @id",
                new { embedding = vector, id }, cancellationToken: cancellationToken));
        }

        await EmbedContent(await InsertEntry("vec-alpha-content", "alpha"));
        await EmbedContent(await InsertEntry("vec-beta-content", "beta"));
        await EmbedStructure(await InsertEntry("vec-alpha-structure", "alpha"));
        await EmbedStructure(await InsertEntry("vec-beta-structure", "beta"));

        // Embedded but project_id IS NULL: must be excluded from every count.
        await EmbedContent(await InsertEntry("vec-null-content", null, "shared"));

        // Never embedded at all: must not appear in either vec leg's counts.
        await InsertEntry("vec-gamma-no-vec", "gamma");
    }

    private async Task<string?> MapJsonAsync(string kind)
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT map_json FROM repair_requests WHERE kind = @kind", new { kind });
    }

    private async Task SeedProjectIdsAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) VALUES " +
            "('jsaa-1', 'jsaa-1', 'jsaa-1', 'seed.md', 'project', 'jsaa', 'ctx-a', 1, 1, 'pending')," +
            "('jsaa-2', 'jsaa-2', 'jsaa-2', 'seed.md', 'project', 'jsaa', 'ctx-a', 2, 2, 'pending')," +
            "('loser-1', 'loser-1', 'loser-1', 'seed.md', 'project', 'job-search-ai-assistant', 'ctx-a', 3, 3, 'pending')," +
            "('loser-2', 'loser-2', 'loser-2', 'seed.md', 'project', 'job-search-ai-assistant', 'ctx-a', 4, 4, 'pending')");
        await connection.ExecuteAsync(
            "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) VALUES " +
            "('job-search-ai-assistant', 'loser-q1', 'loser-q1', 0.7, 3, 3)," +
            "('jsaa', 'winner-q1', 'winner-q1', 0.9, 1, 1)");
    }

    private async Task<long> RequestCountAsync(string kind)
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM repair_requests WHERE kind = @kind", new { kind });
    }
}
