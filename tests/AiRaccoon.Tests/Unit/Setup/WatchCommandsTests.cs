using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Direct tests of the extracted WatchCommands component (enable/disable, scope, concurrency,
///     list, registered, remove). The full behavior contract lives in ConfigCommandsWatchTests;
///     these pin the component seam and its ctor-injected IWatchStore shape.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class WatchCommandsTests
{
    /// <summary>Calls the component method directly — no dispatcher, that is the seam under test.</summary>
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store,
        FakeWatchStore? watchStore = null) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
        {
            var commands = new WatchCommands(watchStore ?? new FakeWatchStore());
            return parsed.CommandPath switch
            {
                ["settings", "watch", "enable"] or ["settings", "watch", "disable"] => commands.SetEnabledAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "ingest", "scope", "add"] => commands.ScopeAddAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "ingest", "scope", "remove"] => commands.ScopeRemoveAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "ingest", "scope", "list"] => commands.ScopeListAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "watch", "concurrency"] => commands.ConcurrencyAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "watch", "list"] => commands.ListAsync(store, streams, ct),
                ["watch", "registered"] => commands.RegisteredAsync(parsed.ParsedCliArgs, streams, ct),
                ["settings", "watch", "remove"] => commands.RemoveAsync(parsed.ParsedCliArgs, store, streams, ct),
                _ => throw new InvalidOperationException($"unhandled: {string.Join(' ', parsed.CommandPath)}")
            };
        });

    [Fact]
    public async Task Registered_ListsSorted_WithCtorWatchStore()
    {
        var store = new FakeWatchStore();
        store.Watches[("acme", "/a/b.md")] = (CreatedAt: 1_700_000_000, LastChangeTs: 1_700_000_100);
        store.Watches[("acme", "/a/c.md")] = (CreatedAt: 1_700_000_200, LastChangeTs: 0);

        var (exit, stdout, _) = await Run(["watch", "registered"], new FakeConfigStore(), store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe(
            "project: acme  path: /a/b.md  registered: 2023-11-14T22:13:20Z  lastChange: 2023-11-14T22:15:00Z\n" +
            "project: acme  path: /a/c.md  registered: 2023-11-14T22:16:40Z  lastChange: never");
    }

    [Fact]
    public async Task Registered_NoWatches_PrintsMessage()
    {
        var (exit, stdout, _) = await Run(["watch", "registered"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("no registered watches");
    }

    [Fact]
    public async Task List_RendersBlockFormat_WithGlobalFallback()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["watch.enabled.global"] = "true",
                ["watch.concurrency.global"] = "4"
            }
        };

        var (exit, stdout, _) = await Run(["settings", "watch", "list"], store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("target: global  enabled: true  concurrency: 4  scope: (none)");
    }

    [Fact]
    public async Task ScopeAdd_NormalizesAndDedups()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["settings", "ingest", "scope", "add", "acme", "/a/../a/b.md"], store);

        exit.ShouldBe(0);
        store.Settings["ingest.scope.acme"].ShouldContain("/a/b.md");
    }

    [Fact]
    public void Constructor_NullWatchStore_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new WatchCommands(null!));
    }

    /// <summary>QA-3: omitting the bool on watch enable/disable must be a parse error — the
    /// System.CommandLine bool argument otherwise defaults to false, so `watch enable X`
    /// silently DISABLES watching for X.</summary>
    [Fact]
    public void Enable_WithoutTheBoolArgument_IsAParseError()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "acme"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    /// <summary>QA-3 control: the explicit form still parses and drives the verb.</summary>
    [Fact]
    public async Task Enable_WithExplicitBool_SetsIt()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["settings", "watch", "enable", "acme", "true"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("enabled");
        store.Settings["watch.enabled.acme"].ShouldBe("true");
    }
}
