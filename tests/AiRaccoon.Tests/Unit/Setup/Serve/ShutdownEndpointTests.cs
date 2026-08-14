using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     POST /shutdown (ADR-0022): token-gated, POST-only, and mapped only on a gated host —
///     an unauthenticated caller is refused with the same answer a wrong token gets.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ShutdownEndpointTests : IDisposable
{
    private const string Token = "test-token-0123456789012345678901234567890123";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-shutdown-endpoint");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task Post_WithoutTheToken_Is401_AndTheServerKeepsServing()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var response = await PostShutdownAsync(port, token: null);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            // Still serving: the refusal cost the caller nothing but the 401.
            host.Services.GetRequiredService<IHostApplicationLifetime>()
                .ApplicationStopping.IsCancellationRequested.ShouldBeFalse();
            using var again = await PostShutdownAsync(port, token: null);
            again.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Post_WithTheWrongToken_AnswersExactlyAsAMissingOne()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var missing = await PostShutdownAsync(port, token: null);
            using var wrong = await PostShutdownAsync(port, "not-the-token");
            using var truncated = await PostShutdownAsync(port, Token[..^1]);

            var bodies = new List<string>();
            foreach (var response in new[] { missing, wrong, truncated })
            {
                response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
                bodies.Add(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            // Identical answers: the refusal must not tell a caller whether a token exists.
            bodies.Distinct(StringComparer.Ordinal).Count().ShouldBe(1);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Post_WithTheToken_IsAccepted_AndStopsTheHost()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var response = await PostShutdownAsync(port, Token);

            // Answered before it stops: the caller learns the request landed.
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await WaitForStoppingAsync(lifetime))
                .ShouldBeTrue("the host never signalled stopping after an accepted shutdown");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_IsNotAllowed()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/shutdown");
            request.Headers.Add(McpTokenGate.HeaderName, Token);
            using var response = await HttpClient.SendAsync(request, TestContext.Current.CancellationToken);

            // A GET is what a browser drive-by would send; only POST stops the server.
            response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Post_IsNotMappedAtAll_WhenTheServerIsUngated()
    {
        // A direct `--transport http` launch mints no token (ADR-0020 non-goal), so it must not
        // expose an unauthenticated shutdown either.
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(UngatedConfig(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var response = await PostShutdownAsync(port, token: null);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>False when the host never signalled stopping, so the caller can say so.</summary>
    private static ValueTask<bool> WaitForStoppingAsync(IHostApplicationLifetime lifetime) =>
        WaitByPolling.WaitForAsync(
            () => ValueTask.FromResult(lifetime.ApplicationStopping.IsCancellationRequested),
            WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, TimeSpan.FromSeconds(10),
            TimeProvider.System, TestContext.Current.CancellationToken);

    private static async Task<HttpResponseMessage> PostShutdownAsync(int port, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/shutdown");
        if (token is not null)
        {
            request.Headers.Add(McpTokenGate.HeaderName, token);
        }

        return await HttpClient.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private ServerConfig GatedConfig(int port) => UngatedConfig(port) with { McpToken = Token };

    private ServerConfig UngatedConfig(int port) => new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
}
