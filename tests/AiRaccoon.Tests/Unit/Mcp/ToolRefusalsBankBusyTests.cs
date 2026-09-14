using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     SQLITE_BUSY (5) / SQLITE_LOCKED (6) is the WP12 write-lock convoy, not an unmapped crash:
///     before this classification a busy bank inside any tool surfaced as EventId 912 (Error, with
///     the exception attached) and the caller saw <c>unexpected-error: SqliteException</c>. The
///     filter walks the exception chain for 5/6 and answers a <c>bank-busy</c> refusal at Warning,
///     carrying no exception — the same one-line shape the extraction pass already logs for the
///     same condition.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolRefusalsBankBusyTests
{
    [Fact]
    public async Task Busy_Code5_ReturnsBankBusyRefusal()
    {
        var result = await ToolRefusals.Filter(Throwing(new SqliteException("database is locked", 5)))(
            Request("memory_search"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("bank-busy:");
        TextOf(result).ShouldContain("another writer holds the lock");
        TextOf(result).ShouldContain("retry the call");
    }

    [Fact]
    public async Task Locked_Code6_ReturnsBankBusyRefusal()
    {
        var result = await ToolRefusals.Filter(Throwing(new SqliteException("database table is locked", 6)))(
            Request("memory_write"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("bank-busy:");
    }

    /// <summary>Dapper or a store helper may wrap the busy error; the chain walk is what keeps that from reverting to 912.</summary>
    [Fact]
    public async Task NestedBusy_ReturnsBankBusyRefusal()
    {
        var wrapped = new InvalidOperationException("write failed",
            new SqliteException("database is locked", 5));

        var result = await ToolRefusals.Filter(Throwing(wrapped))(Request("memory_search"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldStartWith("bank-busy:");
    }

    /// <summary>
    ///     Not every SqliteException is transient: code 26 is a corrupt/mismatched file, a real fault
    ///     that keeps the unmapped shape (type-only text, Error record) rather than being laundered
    ///     into a retry instruction the caller cannot act on.
    /// </summary>
    [Fact]
    public async Task NonBusySqliteError_StaysUnexpected()
    {
        var result = await ToolRefusals.Filter(Throwing(new SqliteException("file is not a database", 26)))(
            Request("memory_stats"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldBe("unexpected-error: SqliteException");
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Throwing(Exception exception) =>
        (_, _) => ValueTask.FromException<CallToolResult>(exception);

    private static RequestContext<CallToolRequestParams> Request(string toolName)
    {
        var server = Substitute.For<McpServer>();
        return new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId("busy-1") },
            new CallToolRequestParams { Name = toolName });
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
