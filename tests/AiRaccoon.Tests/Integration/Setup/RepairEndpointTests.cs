using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.AspNetCore.Builder;
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
