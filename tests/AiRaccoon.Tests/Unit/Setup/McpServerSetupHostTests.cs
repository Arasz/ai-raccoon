using System.Net;
using System.Net.Sockets;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
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
public class McpServerSetupHostTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-host-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

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
        // Free port: 7721 may be held by a live server or a concurrent suite.
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio, FreePort()), [McpTransport.Stdio, McpTransport.Http]);

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

    /// <summary>
    ///     The registered MCP surface is the full 19 tools: 16 memory + 3 watch. Regression
    ///     gate for PR #30 dropping .WithTools&lt;WatchTools&gt;() — host tests previously
    ///     pinned transport shape only, so the watch trio silently vanished from tools/list.
    /// </summary>
    [Fact]
    public void StdioHost_RegistersWatchTools_OnTheMcpSurface()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var toolNames = (options.ToolCollection ?? throw new InvalidOperationException("ToolCollection not configured"))
            .Select(t => t.ProtocolTool.Name).ToList();

        toolNames.Count.ShouldBe(19);
        toolNames.ShouldContain("memory_watch_add");
        toolNames.ShouldContain("memory_watch_status");
        toolNames.ShouldContain("memory_watch_remove");
    }

    private ServerConfig Config(McpTransport transport, int port = 7721) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });

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
