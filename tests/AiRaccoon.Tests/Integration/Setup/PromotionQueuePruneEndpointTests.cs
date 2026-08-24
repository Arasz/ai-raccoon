using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Memory;
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

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     ADR-0075 amendment: `extract prune` reaches the server entirely, both the read-only report
///     and the apply request — the CLI never opens the bank for it. Modelled on
///     <see cref="RepairEndpointTests" />.
/// </summary>
/// <remarks>
///     The request row is counted without the finished_at filter: the maintenance loop's 15s
///     on-demand poll can legitimately apply the request between the POST and the count, so the
///     endpoint's contract is the commit, not the row staying open.
/// </remarks>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueuePruneEndpointTests : IAsyncLifetime
{
    private const string Token = "promotion-queue-prune-endpoint-token";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-promotion-queue-prune-endpoint");
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

    [Fact]
    public async Task GetReport_OnAnUnaffectedBank_ReportsNothingToDo()
    {
        var response = await _client.GetAsync(PromotionQueuePruneProtocol.Path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<PromotionQueueOrphanReport>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().TotalOrphans.ShouldBe(0);
    }

    [Fact]
    public async Task Post_CommitsARequestRow()
    {
        var response = await _client.PostAsync(PromotionQueuePruneProtocol.Path, null, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RequestCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task WithoutTheToken_EveryVerbIsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync(PromotionQueuePruneProtocol.Path, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PostAsync(PromotionQueuePruneProtocol.Path, null, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<long> RequestCountAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM promotion_queue_prune_requests WHERE id = 1");
    }
}
