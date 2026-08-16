using System.Net;
using System.Net.Http.Json;
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
///     WP7 §5.3 (docs/plans/2026-08-16-bank-open-cost-implementation.md): the control-plane
///     settings endpoint, which serves reads and writes alike so a subsystem has one
///     implementation rather than two. Modelled on /shutdown — an endpoint, not an MCP tool, so
///     the single-config-channel constraint on the *tool* surface is untouched.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SettingsEndpointTests : IAsyncLifetime
{
    private const string Token = "settings-endpoint-token";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-settings-endpoint");
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
    public async Task PutThenGet_RoundTripsTheValue()
    {
        (await PutAsync("sweep.threshold", "0.7")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var response = await _client.GetAsync("/settings?key=sweep.threshold", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SettingValue>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Value.ShouldBe("0.7");
    }

    [Fact]
    public async Task Get_ForAnAbsentKey_IsNotFound()
    {
        var response = await _client.GetAsync("/settings?key=nothing.here", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByPrefix_ReturnsOnlyMatchingRows()
    {
        await PutAsync("queryGuard.enabled.global", "false");
        await PutAsync("queryGuard.shadow.global", "true");
        await PutAsync("sweep.threshold", "0.3");

        var response = await _client.GetAsync("/settings?prefix=queryGuard.", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rows = (await response.Content.ReadFromJsonAsync<SettingRows>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Rows;
        rows.Count.ShouldBe(2);
        rows["queryGuard.enabled.global"].ShouldBe("false");
        rows["queryGuard.shadow.global"].ShouldBe("true");
    }

    [Fact]
    public async Task GetByPrefix_WithNoMatch_IsAnEmptySet_NotAnError()
    {
        var response = await _client.GetAsync("/settings?prefix=nothing.", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SettingRows>(TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        await PutAsync("noise.enabled.global", "false");

        var deleted = await _client.DeleteAsync("/settings?key=noise.enabled.global", TestContext.Current.CancellationToken);

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _client.GetAsync("/settings?key=noise.enabled.global", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>Deleting what is not there is how every settings handler already behaves.</summary>
    [Fact]
    public async Task Delete_ForAnAbsentKey_Succeeds()
    {
        var response = await _client.DeleteAsync("/settings?key=never.written", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/settings?key=a&prefix=b")]
    [InlineData("/settings?key=")]
    public async Task Get_WithoutExactlyOneSelector_IsABadRequest(string url)
    {
        var response = await _client.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_WithoutAKey_IsABadRequest()
    {
        var response = await _client.PutAsJsonAsync("/settings", new SettingWrite("", "v"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>
    ///     The endpoint carries secrets (sync credentials, the OpenAI key), so its refusal without
    ///     the token is asserted here too, not only by the route-table guard.
    /// </summary>
    [Fact]
    public async Task WithoutTheToken_EveryVerbIsRefused()
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };

        (await anonymous.GetAsync("/settings?key=a", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.PutAsJsonAsync("/settings", new SettingWrite("a", "b"), TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await anonymous.DeleteAsync("/settings?key=a", TestContext.Current.CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> PutAsync(string key, string value) =>
        _client.PutAsJsonAsync("/settings", new SettingWrite(key, value), TestContext.Current.CancellationToken);
}
