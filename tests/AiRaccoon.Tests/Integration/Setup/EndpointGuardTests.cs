using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7-T2 (docs/plans/2026-08-16-bank-open-cost-implementation.md §5.3): every endpoint the
///     server maps is refused without the loopback token, unless it is on the opt-out list here.
///     The route table is enumerated and each route is actually called, so a new endpoint that
///     nobody added to <c>McpTokenGate</c> fails this — reading <c>GuardedPaths</c> could not.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EndpointGuardTests : IAsyncLifetime
{
    /// <summary>
    ///     Routes that are open by design. /observability answers the liveness probe a client uses
    ///     before it has a token (ADR-0020) and reveals no bank content.
    /// </summary>
    private static readonly string[] OpenByDesign = ["/observability"];

    private const string Token = "route-table-guard-token";

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options) { McpToken = Token });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    [Fact]
    public async Task EveryMappedEndpoint_IsGuarded_OrDeclaredOpen()
    {
        var routes = MappedRoutes();

        routes.ShouldNotBeEmpty("the route table came back empty, so this proves nothing");
        foreach (var route in routes)
        {
            var response = await _client.GetAsync(route, TestContext.Current.CancellationToken);
            if (OpenByDesign.Contains(route, StringComparer.Ordinal))
            {
                response.StatusCode.ShouldNotBe(System.Net.HttpStatusCode.Unauthorized,
                    $"{route} is declared open but the gate refused it");
                continue;
            }

            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized,
                $"{route} is mapped but unguarded — add it to McpTokenGate, or to this test's opt-out list with a reason");
        }
    }

    /// <summary>The opt-out list is only meaningful while every entry is a route that exists.</summary>
    [Fact]
    public void EveryDeclaredOpenRoute_IsStillMapped()
    {
        var routes = MappedRoutes();

        foreach (var open in OpenByDesign)
        {
            routes.ShouldContain(open, $"{open} is on the opt-out list but nothing maps it any more");
        }
    }

    /// <summary>Distinct request paths in the route table, one per pattern rather than per method.</summary>
    private IReadOnlyList<string> MappedRoutes() =>
        [
            .. _app.Services.GetRequiredService<EndpointDataSource>().Endpoints
                .OfType<RouteEndpoint>()
                .Select(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];
}
