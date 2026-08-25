using System.ComponentModel;
using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;
using xRetry.v3;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     What the real proxy process puts on the wire, recorded by the backend it dials: an MCP
///     backend on a real loopback socket, headers captured before the endpoint runs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxyWireE2ETests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("proxy-wire");
    private readonly List<string> _headerNames = [];
    private readonly List<int> _postStatuses = [];
    private WebApplication _backend = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        using var lease = LoopbackPort.Reserve();
        _port = lease.Port;
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
            if (HttpMethods.IsPost(context.Request.Method))
            {
                lock (_postStatuses)
                {
                    _postStatuses.Add(context.Response.StatusCode);
                }
            }
        });
        _backend.MapMcp("/mcp");
        lease.ReleaseForBind();
        await _backend.StartAsync(TestContext.Current.CancellationToken);
        // Ungated on purpose: this fixture records headers, it does not check them. The proxy still
        // reads a token, so mint one the way serve would.
        await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _backend.StopAsync(CancellationToken.None);
        await _backend.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     ADR-0020 and ADR-0021 both rest on this: the proxy propagates no trace context, so
    ///     HttpRequestIn on the backend stays a trace root. Asserted on the wire — Activity.Current
    ///     can be non-null for reasons that have nothing to do with our instrumentation.
    /// </summary>
    [RetryFact]
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
    ///     The second net under the token wiring: this fixture's backend is ungated, so only the
    ///     header on the wire can show the proxy still presents it.
    /// </summary>
    [RetryFact]
    public async Task ForwardedRequests_CarryTheLoopbackToken()
    {
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);

        await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Recorded().ShouldContain(name => name.Equals(McpTokenGate.HeaderName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Clients detect unsupported capabilities by MethodNotFound. On the revisions that map
    ///     JSON-RPC errors onto HTTP statuses the backend answers an unknown method with 404, and the
    ///     SDK converts an error body only when it is application/json — so without the proxy's
    ///     recovery handler the code collapses to -32603 (ADR-0020, "the backend's error status").
    /// </summary>
    [RetryFact]
    public async Task UnknownMethod_KeepsItsMethodNotFoundCode()
    {
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);
        ForgetPostStatuses(); // the handshake negotiates down through 400s of its own

        var failure = await Should.ThrowAsync<McpProtocolException>(async () =>
            await client.SendRequestAsync(
                new JsonRpcRequest { Method = "x/unknown", Id = new RequestId("wire-1") },
                TestContext.Current.CancellationToken));

        // The premise, asserted rather than assumed: on a revision that answered 200 here the code
        // would survive on its own, and this test would keep passing with the handler deleted.
        PostStatuses().ShouldContain(status => status >= 400);
        failure.ErrorCode.ShouldBe(McpErrorCode.MethodNotFound);
    }

    private string[] Recorded()
    {
        lock (_headerNames)
        {
            return [.. _headerNames];
        }
    }

    private int[] PostStatuses()
    {
        lock (_postStatuses)
        {
            return [.. _postStatuses];
        }
    }

    private void ForgetPostStatuses()
    {
        lock (_postStatuses)
        {
            _postStatuses.Clear();
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
