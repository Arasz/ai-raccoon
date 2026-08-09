using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
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
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store,
        FakeWatchStore? watchStore = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var commands = new WatchCommands(watchStore ?? new FakeWatchStore());
        var exit = parsed.CommandPath switch
        {
            ["watch", "enable"] or ["watch", "disable"] => await commands.SetEnabledAsync(parsed.ParseResult, store, stdout, stderr, TestContext.Current.CancellationToken),
            ["watch", "scope", "add"] => await commands.ScopeAddAsync(parsed.ParseResult, store, stdout, TestContext.Current.CancellationToken),
            ["watch", "scope", "remove"] => await commands.ScopeRemoveAsync(parsed.ParseResult, store, stdout, TestContext.Current.CancellationToken),
            ["watch", "scope", "list"] => await commands.ScopeListAsync(parsed.ParseResult, store, stdout, TestContext.Current.CancellationToken),
            ["watch", "concurrency"] => await commands.ConcurrencyAsync(parsed.ParseResult, store, stdout, stderr, TestContext.Current.CancellationToken),
            ["watch", "list"] => await commands.ListAsync(store, stdout, TestContext.Current.CancellationToken),
            ["watch", "registered"] => await commands.RegisteredAsync(parsed.ParseResult, stdout, TestContext.Current.CancellationToken),
            ["watch", "remove"] => await commands.RemoveAsync(parsed.ParseResult, store, stdout, TestContext.Current.CancellationToken),
            _ => throw new InvalidOperationException($"unhandled: {string.Join(' ', parsed.CommandPath)}")
        };
        return (exit, stdout.ToString(), stderr.ToString());
    }

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

        var (exit, stdout, _) = await Run(["watch", "list"], store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("target: global  enabled: true  concurrency: 4  scope: (none)");
    }

    [Fact]
    public async Task ScopeAdd_NormalizesAndDedups()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["watch", "scope", "add", "acme", "/a/../a/b.md"], store);

        exit.ShouldBe(0);
        store.Settings["ingest.scope.acme"].ShouldContain("/a/b.md");
    }

    [Fact]
    public void Constructor_NullWatchStore_ThrowsArgumentNullException()
    {
        Should.Throw<ArgumentNullException>(() => new WatchCommands(null!));
    }
}
