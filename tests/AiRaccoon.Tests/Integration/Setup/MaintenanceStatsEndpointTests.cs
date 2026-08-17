using System.Net;
using System.Net.Http.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     ADR-0075 amendment: `settings maintenance list`'s disk-stats read reaches the server
///     entirely — read-only, unconditional. Modelled on <see cref="RepairEndpointTests" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MaintenanceStatsEndpointTests : IAsyncLifetime
{
    private const string Token = "maintenance-stats-endpoint-token";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-maintenance-stats-endpoint");
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
    public async Task Get_ReturnsLiveBankStats()
    {
        var response = await _client.GetAsync(MaintenanceStatsProtocol.Path, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<BankStats>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().DbBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task WithoutTheToken_IsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync(MaintenanceStatsProtocol.Path, TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
