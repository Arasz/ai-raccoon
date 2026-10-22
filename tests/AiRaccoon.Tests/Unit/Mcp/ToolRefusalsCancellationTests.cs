using System.Text.Json;
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
            Request("query"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("cancelled:");
        TextOf(result).ShouldContain("query");
    }

    /// <summary>The traced stack threw the derived type at the key-resolver gate.</summary>
    [Fact]
    public async Task TaskCanceled_WithLiveToken_ReturnsCancelledErrorResult()
    {
        var result = await ToolRefusals.Filter(Throwing(new TaskCanceledException()))(
            Request("query"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("cancelled:");
    }

    /// <summary>
    ///     The caller gave up: answered like the live-token case; the SDK's stack-trace warn for it is
    ///     dropped at the logging layer (CancelledRequestLoggingTests).
    /// </summary>
    [Fact]
    public async Task TaskCanceled_WithCancelledToken_ReturnsCancelledErrorResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await ToolRefusals.Filter(Throwing(new TaskCanceledException()))(
            Request("query"), cts.Token);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("cancelled:");
        TextOf(result).ShouldContain("query");
    }

    /// <summary>Protocol-level stays protocol-level, even though McpProtocolException derives from McpException.</summary>
    [Fact]
    public async Task McpProtocolException_StillRethrows()
    {
        await Should.ThrowAsync<McpProtocolException>(() =>
            ToolRefusals.Filter(Throwing(new McpProtocolException("boom")))(
                Request("query"), CancellationToken.None).AsTask());
    }

    /// <summary>The cancel line follows the refusal anti-flood shape: one line, no exception attached.</summary>
    [Fact]
    public void CancelledLog_IsInformation_WithEventId_AndNoException()
    {
        var logger = new FakeLogger();

        ToolRefusals.Log.ToolCancelled(logger, "query", "request was cancelled");

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Level.ShouldBe(LogLevel.Information);
        record.Id.Id.ShouldBe(913);
        record.Message.ShouldContain("query");
        record.Exception.ShouldBeNull();
    }

    /// <summary>A caller that gave up leaves one line saying which call timed out and on what query — no second cancel line.</summary>
    [Fact]
    public async Task OperationCanceled_WithCancelledToken_LogsTheTimeoutWithTheQueryLength()
    {
        var logger = new FakeLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var arguments = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(new string('q', 4181))
        };

        await ToolRefusals.Filter(Throwing(new TaskCanceledException()))(
            Request("query", arguments, logger), cts.Token);

        logger.Collector.Count.ShouldBe(1);
        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Id.Id.ShouldBe(914);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldContain("query");
        record.Message.ShouldContain("timed out");
        record.Message.ShouldContain("query of 4181 chars");
        record.Exception.ShouldBeNull();
        // The length is the record's own QueryLength field — raw digits aggregation can filter and
        // aggregate on — not a presentation clause. (The fake stringifies state values.)
        record.StructuredState!.Single(kv => kv.Key == "QueryLength").Value.ShouldBe("4181");
    }

    /// <summary>A tool with no query argument logs the timeout without a query clause — its own event 915: one
    /// EventId per [LoggerMessage] method is unconditional (docs/reference/logging-event-ids.md), so the two
    /// renderings of one timeout cannot share 914.</summary>
    [Fact]
    public async Task OperationCanceled_WithCancelledToken_NoQuery_LogsTheTimeoutWithoutAQueryClause()
    {
        var logger = new FakeLogger();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await ToolRefusals.Filter(Throwing(new OperationCanceledException()))(
            Request("sync_to_cloud", null, logger), cts.Token);

        var record = logger.Collector.LatestRecord;
        record.ShouldNotBeNull();
        record.Id.Id.ShouldBe(915);
        record.Message.ShouldContain("sync_to_cloud");
        record.Message.ShouldNotContain("query");
        record.StructuredState!.ShouldNotContain(kv => kv.Key == "QueryLength");
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Throwing(Exception exception) =>
        (_, _) => ValueTask.FromException<CallToolResult>(exception);

    private static RequestContext<CallToolRequestParams> Request(string toolName) => Request(toolName, null, null);

    private static RequestContext<CallToolRequestParams> Request(string toolName,
        IDictionary<string, JsonElement>? arguments, ILogger? logger)
    {
        var server = Substitute.For<McpServer>();
        var context = new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId("cancel-1") },
            new CallToolRequestParams { Name = toolName, Arguments = arguments });
        if (logger is not null)
        {
            var factory = Substitute.For<ILoggerFactory>();
            factory.CreateLogger(Arg.Any<string>()).Returns(logger);
            var services = Substitute.For<IServiceProvider>();
            services.GetService(typeof(ILoggerFactory)).Returns(factory);
            context.Services = services;
        }
        return context;
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
