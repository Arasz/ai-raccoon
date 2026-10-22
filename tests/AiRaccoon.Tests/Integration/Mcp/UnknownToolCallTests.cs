using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     A call naming a tool this server does not have must come back as a normal tool error whose
///     message says which tool to call — not as an escaping protocol error. The SDK throws
///     <c>McpProtocolException("Unknown tool: 'x'")</c> for a name it cannot resolve, the request
///     handler then logs a warn-level "request handler failed", and the caller is left with an
///     opaque failure it cannot act on. A client that still speaks the pre-2.0.0 names is the case
///     that matters: the message has to name the tool to call instead.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class UnknownToolCallTests
{
    /// <summary>
    ///     A name the 2.0.0 rename took away: <c>memory_search</c> is <c>query</c> now, and the
    ///     message has to say so.
    /// </summary>
    [RetryFact]
    public async Task RenamedToolCall_NamesTheToolToCall()
    {
        var (result, logs) = await CallAsync("memory_search");

        result.IsError.ShouldBe(true);
        var text = Text(result);
        text.ShouldStartWith("unknown-tool:");
        text.ShouldContain("'query'");

        NoErrorLogs(logs);
    }

    /// <summary>A name this server never had: the message says so, and says what to do instead.</summary>
    [RetryFact]
    public async Task UnknownToolCall_SaysTheServerHasNoSuchTool()
    {
        var (result, logs) = await CallAsync("no_such_tool");

        result.IsError.ShouldBe(true);
        var text = Text(result);
        text.ShouldStartWith("unknown-tool:");
        text.ShouldContain("no_such_tool");

        NoErrorLogs(logs);
    }

    private static async Task<(CallToolResult Result, FakeLoggerProvider Logs)> CallAsync(string toolName)
    {
        var dataRoot = TestData.CreateTempRoot($"unknown-tool-{toolName}");
        try
        {
            var logs = new FakeLoggerProvider();
            await using var server = await RealMcpServerFixture.StartAsync(dataRoot,
                TestContext.Current.CancellationToken, logs);
            var client = await server.ConnectAsync("unknown-tool-test", TestContext.Current.CancellationToken);

            var result = await client.CallToolAsync(toolName,
                new Dictionary<string, object?> { ["projectId"] = "acme" },
                cancellationToken: TestContext.Current.CancellationToken);

            return (result, logs);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>
    ///     A client naming a tool that does not exist is the client's mistake, not a server fault:
    ///     the refusal is logged like every other expected refusal, never at Error.
    /// </summary>
    private static void NoErrorLogs(FakeLoggerProvider logs)
    {
        var errors = logs.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Error).ToList();
        errors.ShouldBeEmpty(string.Join('\n', errors.Select(e => $"{e.Category}: {e.Message}")));
    }
}
