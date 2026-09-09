using AiRaccoon.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using NSubstitute;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     A cancelled tool call is a caller-driven outcome, not a crash: without this mapping the
///     <see cref="OperationCanceledException" /> escapes to <c>McpServerImpl</c>, which logs
///     warn "request handler failed" for what is just a client that went away.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolRefusalsCancellationTests
{
    [Fact]
    public async Task OperationCanceled_WithLiveToken_ReturnsCancelledErrorResult()
    {
        var result = await ToolRefusals.Filter(Throwing(new OperationCanceledException()))(
            Request("memory_search"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("cancelled:");
        TextOf(result).ShouldContain("memory_search");
    }

    /// <summary>The traced stack threw the derived type at the key-resolver gate.</summary>
    [Fact]
    public async Task TaskCanceled_WithLiveToken_ReturnsCancelledErrorResult()
    {
        var result = await ToolRefusals.Filter(Throwing(new TaskCanceledException()))(
            Request("memory_search"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("cancelled:");
    }

    /// <summary>Dead connection: writing an answer to a gone client is pointless, so rethrow.</summary>
    [Fact]
    public async Task OperationCanceled_WithCancelledToken_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ToolRefusals.Filter(Throwing(new OperationCanceledException()))(
                Request("memory_search"), cts.Token).AsTask());
    }

    /// <summary>Protocol-level stays protocol-level, even though McpProtocolException derives from McpException.</summary>
    [Fact]
    public async Task McpProtocolException_StillRethrows()
    {
        await Should.ThrowAsync<McpProtocolException>(() =>
            ToolRefusals.Filter(Throwing(new McpProtocolException("boom")))(
                Request("memory_search"), CancellationToken.None).AsTask());
    }

    /// <summary>The cancel line follows the refusal anti-flood shape: one line, no exception attached.</summary>
    [Fact]
    public void CancelledLog_IsInformation_WithEventId_AndNoException()
    {
        var logger = new FakeLogger();

        ToolRefusals.Log.ToolCancelled(logger, "memory_search", "request was cancelled");

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Information);
        record.Id.Id.ShouldBe(913);
        record.Message.ShouldContain("memory_search");
        record.Exception.ShouldBeNull();
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Throwing(Exception exception) =>
        (_, _) => ValueTask.FromException<CallToolResult>(exception);

    private static RequestContext<CallToolRequestParams> Request(string toolName)
    {
        var server = Substitute.For<McpServer>();
        return new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId("cancel-1") },
            new CallToolRequestParams { Name = toolName });
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
