using System.Net;
using System.Net.Sockets;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Host-shape contract after the MCP setup refactor: stdio-only launches run on a
///     plain app host with no web server, HTTP/S launches run on a web host bound to the
///     configured port (never the ASP.NET default 5000), and a combined stdio+http set
///     keeps the web host with stdio attached.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class McpServerSetupHostTests
{
    [Fact]
    public async Task StdioOnlyHost_HasNoWebServer_AndStartsWithTheDefaultPortHeld()
    {
        using var blocker = TryHoldLoopbackPort(5000);

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetService(typeof(IServer)).ShouldBeNull();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HttpHost_BindsTheConfiguredPort_NotTheDefault5000()
    {
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        await host.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var urls = ((WebApplication)host).Urls;
            urls.ShouldContain(url => url.Contains($":{port}", StringComparison.Ordinal));
            urls.ShouldNotContain(url => url.Contains(":5000", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task HttpHost_WithPortZero_BindsAnEphemeralPort()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, 0));

        await host.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var urls = ((WebApplication)host).Urls;
            urls.ShouldNotBeEmpty();
            urls.ShouldNotContain(url => url.Contains(":5000", StringComparison.Ordinal) || url.Contains(":7721", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BothTransports_CreateWebHostWithStdio()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio), [McpTransport.Stdio, McpTransport.Http]);

        host.Services.GetService(typeof(IServer)).ShouldNotBeNull();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_HttpHost_StartsAndStopsCleanly()
    {
        var config = Config(McpTransport.Http, FreePort());
        var host = McpServerSetup.CreateServerHost(config);

        var runTask = host.RunAsync(config, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await runTask;
    }

    private static ServerConfig Config(McpTransport transport, int port = 7721) => ServerConfig.Build(new CliOptions(transport.ToString().ToLowerInvariant(), null, null, port));

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TcpListener? TryHoldLoopbackPort(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            listener.Stop();
            return null;
        }
    }
}
