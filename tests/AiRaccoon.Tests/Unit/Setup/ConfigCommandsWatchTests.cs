using System.Text.Json;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.Unit.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Watch-config commands pin the settings-key contract the file-watcher branch reads:
///     watch.enabled/ingest.scope/watch.concurrency, each keyed by project or "global", with
///     project rows winning over global and scope paths normalized via Path.GetFullPath.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsWatchTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store, FakeWatchStore? watchStore = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ConfigCommands(watch: new WatchCommands(watchStore ?? new FakeWatchStore())).RunAsync(parsed.CommandPath, parsed.ParsedCliArgs, store, stdout, stderr, TextReader.Null,
            ctx: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }


    [Fact]
    public async Task WatchEnable_WritesTrueRow_ForProject()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["watch", "enable", "acme", "true"], store);

        exit.ShouldBe(0);
        store.Settings[WatchConfigKeys.EnabledProject("acme")].ShouldBe("true");
    }

    [Fact]
    public async Task WatchEnableStar_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        await Run(["watch", "enable", "*", "true"], store);

        store.Settings[WatchConfigKeys.EnabledGlobal].ShouldBe("true");
    }

    [Fact]
    public async Task WatchEnableStar_WithEmptyScopeAllowlist_PrintsAddScopeMessage()
    {
        var store = new FakeConfigStore();

        var (exit, _, err) = await Run(["watch", "enable", "*", "true"], store);

        exit.ShouldBe(0);
        err.ShouldContain("ingest scope add '*'");
    }

    [Fact]
    public async Task WatchEnableStar_WithScopesConfigured_PrintsNoHint()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [IngestScopeKeys.ScopeGlobal] = "[\"/a\"]"
            }
        };

        var (_, _, err) = await Run(["watch", "enable", "*", "true"], store);

        err.ShouldBeEmpty();
    }

    [Fact]
    public async Task WatchDisable_WritesFalseRow_ForProject()
    {
        var store = new FakeConfigStore();

        await Run(["watch", "disable", "acme", "false"], store);

        store.Settings[WatchConfigKeys.EnabledProject("acme")].ShouldBe("false");
    }


    [Fact]
    public async Task WatchScopeAdd_NormalizesToAbsolutePath()
    {
        var store = new FakeConfigStore();
        var expected = Path.GetFullPath("rel/notes");

        var (exit, stdout, _) = await Run(["watch", "scope", "add", "acme", "rel/notes"], store);

        exit.ShouldBe(0);
        store.Settings[IngestScopeKeys.ScopeProject("acme")].ShouldBe($"[{JsonSerializer.Serialize(expected)}]");
        stdout.ShouldContain(expected);
    }

    [Fact]
    public async Task WatchScopeAdd_DedupsAndReSorts()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [IngestScopeKeys.ScopeProject("acme")] = "[\"/b\"]"
            }
        };

        await Run(["watch", "scope", "add", "acme", "/a"], store);
        await Run(["watch", "scope", "add", "acme", "/b"], store);

        store.Settings[IngestScopeKeys.ScopeProject("acme")].ShouldBe("[\"/a\",\"/b\"]");
    }

    [Fact]
    public async Task WatchScopeAddStar_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        await Run(["watch", "scope", "add", "*", "/a"], store);

        store.Settings[IngestScopeKeys.ScopeGlobal].ShouldBe("[\"/a\"]");
    }

    [Fact]
    public async Task WatchScopeRemove_RemovesOnePath_KeepingOrderAndDedup()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [IngestScopeKeys.ScopeProject("acme")] = "[\"/a\",\"/b\",\"/c\"]"
            }
        };

        await Run(["watch", "scope", "remove", "acme", "/b"], store);

        store.Settings[IngestScopeKeys.ScopeProject("acme")].ShouldBe("[\"/a\",\"/c\"]");
    }

    [Fact]
    public async Task WatchScopeRemove_LastPath_DeletesTheRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [IngestScopeKeys.ScopeProject("acme")] = "[\"/a\"]"
            }
        };

        await Run(["watch", "scope", "remove", "acme", "/a"], store);

        store.Settings.ShouldNotContainKey(IngestScopeKeys.ScopeProject("acme"));
    }

    [Fact]
    public async Task WatchScopeList_PrintsOnePathPerLine()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [IngestScopeKeys.ScopeProject("acme")] = "[\"/a\",\"/b\"]"
            }
        };

        var (exit, stdout, _) = await Run(["watch", "scope", "list", "acme"], store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("/a\n/b");
    }

    [Fact]
    public async Task WatchScopeList_NoRow_PrintsNothing()
    {
        var (exit, stdout, _) = await Run(["watch", "scope", "list", "acme"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.ShouldBeEmpty();
    }


    [Fact]
    public async Task WatchConcurrency_WritesRow()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["watch", "concurrency", "acme", "8"], store);

        exit.ShouldBe(0);
        store.Settings[WatchConfigKeys.ConcurrencyProject("acme")].ShouldBe("8");
    }

    [Fact]
    public async Task WatchConcurrencyStar_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        await Run(["watch", "concurrency", "*", "4"], store);

        store.Settings[WatchConfigKeys.ConcurrencyGlobal].ShouldBe("4");
    }

    [Fact]
    public async Task WatchConcurrency_OutOfRange_ReturnsError()
    {
        foreach (var value in new[] { "0", "17" })
        {
            var store = new FakeConfigStore();

            var (exit, _, err) = await Run(["watch", "concurrency", "acme", value], store);

            exit.ShouldBe(1);
            err.ShouldContain("1..16");
            store.Settings.ShouldNotContainKey(WatchConfigKeys.ConcurrencyProject("acme"));
        }
    }


    [Fact]
    public async Task WatchList_ShowsResolvedValues_PerTarget()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledGlobal] = "true",
                [WatchConfigKeys.ConcurrencyProject("acme")] = "2",
                [IngestScopeKeys.ScopeProject("acme")] = "[\"/a\"]"
            }
        };

        var (exit, stdout, _) = await Run(["watch", "list"], store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe(
            "target: acme  enabled: true  concurrency: 2  scope:\n  /a\n" +
            "target: global  enabled: true  concurrency: 4  scope: (none)");
    }

    [Fact]
    public async Task WatchList_ProjectRow_WinsOverGlobal()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledGlobal] = "false",
                [WatchConfigKeys.EnabledProject("acme")] = "true",
                [WatchConfigKeys.ConcurrencyGlobal] = "16",
                [WatchConfigKeys.ConcurrencyProject("acme")] = "8"
            }
        };

        var (_, stdout, _) = await Run(["watch", "list"], store);

        stdout.Trim().ShouldBe(
            "target: acme  enabled: true  concurrency: 8  scope: (none)\n" +
            "target: global  enabled: false  concurrency: 16  scope: (none)");
    }

    [Fact]
    public async Task WatchList_NoRows_PrintsOnlyGlobalDefaults()
    {
        var (exit, stdout, _) = await Run(["watch", "list"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("target: global  enabled: false  concurrency: 4  scope: (none)");
    }

    [Fact]
    public async Task WatchList_Ordering_IsOrdinalByTargetName()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledProject("CLAUDE.md")] = "true",
                [WatchConfigKeys.EnabledProject("acme")] = "true",
                [WatchConfigKeys.EnabledGlobal] = "true"
            }
        };

        var (_, stdout, _) = await Run(["watch", "list"], store);

        stdout.Trim().ShouldBe(
            "target: CLAUDE.md  enabled: true  concurrency: 4  scope: (none)\n" +
            "target: acme  enabled: true  concurrency: 4  scope: (none)\n" +
            "target: global  enabled: true  concurrency: 4  scope: (none)");
    }

    [Fact]
    public async Task WatchList_OnlyEnabledRow_ShowsResolvedGlobalScope()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledProject("CLAUDE.md")] = "true",
                [IngestScopeKeys.ScopeGlobal] = "[\"/x\"]"
            }
        };

        var (_, stdout, _) = await Run(["watch", "list"], store);

        stdout.Trim().ShouldBe(
            "target: CLAUDE.md  enabled: true  concurrency: 4  scope:\n  /x\n" +
            "target: global  enabled: false  concurrency: 4  scope:\n  /x");
    }


    [Fact]
    public async Task WatchRegistered_ListsAllRegistrations_SortedByProjectThenPath()
    {
        var watchStore = new FakeWatchStore();
        watchStore.Watches[("zeta", "/z")] = (1_700_000_000, 0);
        watchStore.Watches[("acme", "/b")] = (1_700_000_000, 0);
        watchStore.Watches[("acme", "/a")] = (1_700_000_000, 2_000_000_000);

        var (exit, stdout, err) = await Run(["watch", "registered"], new FakeConfigStore(), watchStore);

        exit.ShouldBe(0);
        err.ShouldBeEmpty();
        stdout.Trim().ShouldBe(
            "project: acme  path: /a  registered: 2023-11-14T22:13:20Z  lastChange: 2033-05-18T03:33:20Z\n" +
            "project: acme  path: /b  registered: 2023-11-14T22:13:20Z  lastChange: never\n" +
            "project: zeta  path: /z  registered: 2023-11-14T22:13:20Z  lastChange: never");
    }

    [Fact]
    public async Task WatchRegistered_ProjectFilter_LimitsToProject()
    {
        var watchStore = new FakeWatchStore();
        watchStore.Watches[("acme", "/a")] = (1_700_000_000, 0);
        watchStore.Watches[("acme", "/b")] = (1_700_000_000, 0);
        watchStore.Watches[("zeta", "/z")] = (1_700_000_000, 0);

        var (exit, stdout, err) = await Run(["watch", "registered", "acme"], new FakeConfigStore(), watchStore);

        exit.ShouldBe(0);
        err.ShouldBeEmpty();
        stdout.Trim().ShouldBe(
            "project: acme  path: /a  registered: 2023-11-14T22:13:20Z  lastChange: never\n" +
            "project: acme  path: /b  registered: 2023-11-14T22:13:20Z  lastChange: never");

        var (noMatchExit, noMatchStdout, _) = await Run(["watch", "registered", "nope"], new FakeConfigStore(), watchStore);
        noMatchExit.ShouldBe(0);
        noMatchStdout.Trim().ShouldBe("no registered watches");
    }

    [Fact]
    public async Task WatchRegistered_NoRows_PrintsNoRegisteredWatches()
    {
        var (exit, stdout, err) = await Run(["watch", "registered"], new FakeConfigStore(), new FakeWatchStore());

        exit.ShouldBe(0);
        err.ShouldBeEmpty();
        stdout.Trim().ShouldBe("no registered watches");
    }

    [Fact]
    public async Task WatchRegistered_ReadsOnlyTheWatchesTable()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledGlobal] = "true",
                [IngestScopeKeys.ScopeGlobal] = "[\"/x\"]",
                [WatchConfigKeys.ConcurrencyGlobal] = "8"
            }
        };

        var (exit, stdout, _) = await Run(["watch", "registered"], store, new FakeWatchStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("no registered watches");
    }


    [Fact]
    public async Task WatchRemove_DeletesEnabledScopeAndConcurrencyRows_ForTarget()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledProject("acme")] = "true",
                [IngestScopeKeys.ScopeProject("acme")] = "[\"/a\"]",
                [WatchConfigKeys.ConcurrencyProject("acme")] = "8",
                [WatchConfigKeys.EnabledProject("zeta")] = "true"
            }
        };

        var (exit, stdout, _) = await Run(["watch", "remove", "acme"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("removed");
        store.Settings.Keys.ShouldNotContain(WatchConfigKeys.EnabledProject("acme"));
        store.Settings.Keys.ShouldNotContain(IngestScopeKeys.ScopeProject("acme"));
        store.Settings.Keys.ShouldNotContain(WatchConfigKeys.ConcurrencyProject("acme"));
        store.Settings.Keys.ShouldContain(WatchConfigKeys.EnabledProject("zeta"));
    }

    [Fact]
    public async Task WatchRemove_Star_DeletesGlobalRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledGlobal] = "true",
                [IngestScopeKeys.ScopeGlobal] = "[\"/a\"]",
                [WatchConfigKeys.ConcurrencyGlobal] = "8"
            }
        };

        var (exit, stdout, _) = await Run(["watch", "remove", "*"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("removed");
        store.Settings.Keys.ShouldNotContain(WatchConfigKeys.EnabledGlobal);
        store.Settings.Keys.ShouldNotContain(IngestScopeKeys.ScopeGlobal);
        store.Settings.Keys.ShouldNotContain(WatchConfigKeys.ConcurrencyGlobal);
    }

    [Fact]
    public async Task WatchRemove_NoRows_IsExitZeroNoOp()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["watch", "remove", "acme"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("removed");
        store.Settings.ShouldBeEmpty();
    }
}
