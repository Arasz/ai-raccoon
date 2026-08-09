using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     What the real proxy process puts on the wire, recorded by the backend it dials: an MCP
///     backend on a real loopback socket, headers captured before the endpoint runs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProxyWireE2ETests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("proxy-wire");
    private readonly List<string> _headerNames = [];
    private WebApplication _backend = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        _port = AiRaccoonProcess.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, _port));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<PingTools>();
        _backend = builder.Build();
        _backend.Use(async (context, next) =>
        {
            lock (_headerNames)
            {
                _headerNames.AddRange(context.Request.Headers.Select(header => header.Key));
            }

            await next();
        });
        _backend.MapMcp("/mcp");
        await _backend.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _backend.StopAsync(CancellationToken.None);
        await _backend.DisposeAsync();
        try
        {
            Directory.Delete(_dataRoot, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    /// <summary>
    ///     ADR-0020 and ADR-0021 both rest on this: the proxy propagates no trace context, so
    ///     HttpRequestIn on the backend stays a trace root. Asserted on the wire — Activity.Current
    ///     can be non-null for reasons that have nothing to do with our instrumentation.
    /// </summary>
    [Fact]
    public async Task ForwardedRequests_CarryNoTraceparent()
    {
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).ShouldBe(["ping"]);
        var recorded = Recorded();
        recorded.ShouldNotBeEmpty();
        recorded.ShouldNotContain(name => name.Equals("traceparent", StringComparison.OrdinalIgnoreCase));
        recorded.ShouldNotContain(name => name.Equals("tracestate", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Clients detect unsupported capabilities by MethodNotFound. The backend answers an unknown
    ///     method with an error status whose body the SDK only converts when it is application/json,
    ///     so without the proxy's recovery handler both code and message collapse to -32603.
    /// </summary>
    [Fact]
    public async Task UnknownMethod_KeepsItsMethodNotFoundCode()
    {
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);

        var failure = await Should.ThrowAsync<McpProtocolException>(async () =>
            await client.SendRequestAsync(
                new JsonRpcRequest { Method = "x/unknown", Id = new RequestId("wire-1") },
                TestContext.Current.CancellationToken));

        failure.ErrorCode.ShouldBe(McpErrorCode.MethodNotFound);
    }

    private string[] Recorded()
    {
        lock (_headerNames)
        {
            return [.. _headerNames];
        }
    }

    [McpServerToolType]
    public sealed class PingTools
    {
        [McpServerTool(Name = "ping")]
        [Description("Answers pong.")]
        public static string Ping() => "pong";
    }
}
