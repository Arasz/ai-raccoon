using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Ingestion;
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

    private async Task<long> RequestCountAsync(string kind)
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM repair_requests WHERE kind = @kind", new { kind });
    }
}
