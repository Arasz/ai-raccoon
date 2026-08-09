using System.Net;
using System.Net.Sockets;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Serve;
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
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
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
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
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
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var response = await PostShutdownAsync(port, Token);

            // Answered before it stops: the caller learns the request landed.
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await WaitForStoppingAsync(lifetime);
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Get_IsNotAllowed()
    {
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(GatedConfig(port));
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
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(UngatedConfig(port));
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

    private static async Task WaitForStoppingAsync(IHostApplicationLifetime lifetime)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

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

    private ServerConfig UngatedConfig(int port) =>
        new(port, McpTransport.Http, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
