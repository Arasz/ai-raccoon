using System.Text.RegularExpressions;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Access;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tests.TestHelpers;
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
///     otherwise. Access-denied carries its remedy in MemoryAccessGuard (its only raiser); the scope
///     and watch families get theirs at the wire.
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
            "watching-disabled: Watching is disabled for project 'acme'. Run 'ai-raccoon settings watch enable <projectId|*> true' to enable it.");
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
        ToolRefusals.RefusalRemedies.Keys.ShouldAllBe(p => prefixes.Contains(p));
    }

    /// <summary>
    ///     Positive control: a remedy naming a typo'd verb, a dropped required argument, or a command
    ///     that plain doesn't exist must fail here — asserting non-empty text would not catch that.
    ///     Every 'ai-raccoon ...' command quoted in <see cref="ToolRefusals.RefusalRemedies" /> is
    ///     parsed, placeholder swapped for a concrete value, against the real CLI tree.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusalRemedyCommands))]
    public void RefusalRemedyCommand_ParsesAgainstTheRealCliTree(string command)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(SubstitutePlaceholders(command).Split(' '));

        parseResult.Errors.Select(e => e.Message).ShouldBeEmpty($"'{command}' does not parse against the real CLI tree");
    }

    public static TheoryData<string> RefusalRemedyCommands()
    {
        var data = new TheoryData<string>();
        foreach (var command in ExtractCliCommands(string.Concat(ToolRefusals.RefusalRemedies.Values)))
        {
            data.Add(command);
        }

        return data;
    }

    /// <summary>
    ///     Same positive control for access-denied: the remedy is built at runtime with the real
    ///     projectId/mode, so the commands are pulled from the live exception message, not a copy.
    /// </summary>
    [Fact]
    public async Task AccessDeniedRemedy_NamesCommandsThatParseAgainstTheRealCliTree()
    {
        var guard = new MemoryAccessGuard(new EmptySettingsStore());

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            guard.EnsureAsync("acme", AccessRequirement.Destructive, "memory_delete", CancellationToken.None));

        var commands = ExtractCliCommands(ex.Message).ToArray();
        commands.ShouldNotBeEmpty("the access-denied message should quote at least one remedy command");
        foreach (var command in commands)
        {
            var parseResult = CliCommandTree.BuildFullRootCommand().Parse(command.Split(' '));
            parseResult.Errors.Select(e => e.Message).ShouldBeEmpty($"'{command}' does not parse against the real CLI tree");
        }
    }

    /// <summary>Every single-quoted 'ai-raccoon ...' command in a message, with the binary name stripped.</summary>
    private static IEnumerable<string> ExtractCliCommands(string text) =>
        Regex.Matches(text, "'ai-raccoon ([^']+)'").Select(m => m.Groups[1].Value);

    private static string SubstitutePlaceholders(string args) =>
        args.Replace("<projectId|*>", "*", StringComparison.Ordinal)
            .Replace("<path>", "/tmp/x", StringComparison.Ordinal);

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

    /// <summary>Permits every guarded call except mode resolution stays default rw — no settings rows at all.</summary>
    private sealed class EmptySettingsStore : FakeMemoryStore
    {
        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
