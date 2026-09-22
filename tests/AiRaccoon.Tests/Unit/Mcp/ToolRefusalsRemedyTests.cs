using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     F53: the refusals that gate the degradation lifecycle must name the remedy that lifts them,
///     the way the invalid-params siblings already do — an agent cannot tell its human what to run
///     otherwise. Access-denied carries its remedy in MemoryAccessGuard (its only raiser, covered by
///     AccessModeGuardTests); the scope and watch families get theirs at the wire.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolRefusalsRemedyTests
{
    [Fact]
    public async Task PathOutsideScope_Refusal_NamesTheIngestScopeRemedy()
    {
        var result = await ToolRefusals.Filter(Throwing(new PathOutsideScopeException("/etc/passwd")))(
            Request("memory_ingest_file"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldBe(
            "path-outside-scope: Path '/etc/passwd' is outside the ingest scope. Run 'ai-raccoon settings ingest scope add <projectId|*> <path>' to allow it.");
    }

    [Fact]
    public async Task WatchDisabled_Refusal_NamesTheWatchEnableRemedy()
    {
        var result = await ToolRefusals.Filter(Throwing(new WatchDisabledException("acme")))(
            Request("memory_watch_add"), CancellationToken.None);

        result.IsError.ShouldBe(true);
        TextOf(result).ShouldBe(
            "watching-disabled: Watching is disabled for project 'acme'. Run 'ai-raccoon settings watch enable <projectId|*>' to enable it.");
    }

    /// <summary>A refusal with no remedy entry must not grow one silently — the table is the whole set.</summary>
    [Fact]
    public async Task RefusalWithoutARemediedPrefix_StaysUnchanged()
    {
        var result = await ToolRefusals.Filter(Throwing(new PathNotFoundException("/missing")))(
            Request("memory_ingest_file"), CancellationToken.None);

        TextOf(result).ShouldBe("path-not-found: Path '/missing' does not exist.");
    }

    /// <summary>Derive gate: every remedy key is a real mapped prefix, so the table cannot drift onto a typo.</summary>
    [Fact]
    public void RemedyKeys_AreMappedRefusalPrefixes()
    {
        var prefixes = ToolRefusals.RefusalPrefixes.Values.ToHashSet(StringComparer.Ordinal);
        ToolRefusals.RefusalRemedies.Keys.ShouldAllBe(prefixes.Contains);
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Throwing(Exception exception) =>
        (_, _) => ValueTask.FromException<CallToolResult>(exception);

    private static RequestContext<CallToolRequestParams> Request(string toolName)
    {
        var server = Substitute.For<McpServer>();
        return new RequestContext<CallToolRequestParams>(server,
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId("remedy-1") },
            new CallToolRequestParams { Name = toolName });
    }

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}