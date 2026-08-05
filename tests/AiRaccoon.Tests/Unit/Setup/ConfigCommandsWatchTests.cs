using System.Text.Json;
using AiRaccoon.Core.Watch;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Watch-config commands pin the exact settings-key contract the file-watcher branch
///     reads: watch.enabled.global|{projectId} ("true"/"false"),
///     watch.scope.global|{projectId} (JSON array of absolute paths),
///     watch.concurrency.global|{projectId} (1..16, default 4). Project rows win over
///     global; paths normalize with Path.GetFullPath semantics (add = dedup + re-sort).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsWatchTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        var parsed = CliArgs.Parse(args);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr,
            TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // ── enable / disable ──

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
        err.ShouldContain("scope");
    }

    [Fact]
    public async Task WatchEnableStar_WithScopesConfigured_PrintsNoHint()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.ScopeGlobal] = "[\"/a\"]"
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

    // ── scope add / remove / list ──

    [Fact]
    public async Task WatchScopeAdd_NormalizesToAbsolutePath()
    {
        var store = new FakeConfigStore();
        var expected = Path.GetFullPath("rel/notes");

        var (exit, stdout, _) = await Run(["watch", "scope", "add", "acme", "rel/notes"], store);

        exit.ShouldBe(0);
        store.Settings[WatchConfigKeys.ScopeProject("acme")].ShouldBe($"[{JsonSerializer.Serialize(expected)}]");
        stdout.ShouldContain(expected);
    }

    [Fact]
    public async Task WatchScopeAdd_DedupsAndReSorts()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.ScopeProject("acme")] = "[\"/b\"]"
            }
        };

        await Run(["watch", "scope", "add", "acme", "/a"], store);
        await Run(["watch", "scope", "add", "acme", "/b"], store);

        store.Settings[WatchConfigKeys.ScopeProject("acme")].ShouldBe("[\"/a\",\"/b\"]");
    }

    [Fact]
    public async Task WatchScopeAddStar_WritesGlobalRow()
    {
        var store = new FakeConfigStore();

        await Run(["watch", "scope", "add", "*", "/a"], store);

        store.Settings[WatchConfigKeys.ScopeGlobal].ShouldBe("[\"/a\"]");
    }

    [Fact]
    public async Task WatchScopeRemove_RemovesOnePath_KeepingOrderAndDedup()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.ScopeProject("acme")] = "[\"/a\",\"/b\",\"/c\"]"
            }
        };

        await Run(["watch", "scope", "remove", "acme", "/b"], store);

        store.Settings[WatchConfigKeys.ScopeProject("acme")].ShouldBe("[\"/a\",\"/c\"]");
    }

    [Fact]
    public async Task WatchScopeRemove_LastPath_DeletesTheRow()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.ScopeProject("acme")] = "[\"/a\"]"
            }
        };

        await Run(["watch", "scope", "remove", "acme", "/a"], store);

        store.Settings.ShouldNotContainKey(WatchConfigKeys.ScopeProject("acme"));
    }

    [Fact]
    public async Task WatchScopeList_PrintsOnePathPerLine()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.ScopeProject("acme")] = "[\"/a\",\"/b\"]"
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

    // ── concurrency ──

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

    // ── watch list ──

    [Fact]
    public async Task WatchList_ShowsResolvedValues_PerTarget()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [WatchConfigKeys.EnabledGlobal] = "true",
                [WatchConfigKeys.ConcurrencyProject("acme")] = "2",
                [WatchConfigKeys.ScopeProject("acme")] = "[\"/a\"]"
            }
        };

        var (exit, stdout, _) = await Run(["watch", "list"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("global: enabled=true concurrency=4 scope=[]");
        stdout.ShouldContain("acme: enabled=true concurrency=2 scope=[\"/a\"]");
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

        stdout.ShouldContain("global: enabled=false concurrency=16 scope=[]");
        stdout.ShouldContain("acme: enabled=true concurrency=8 scope=[]");
    }

    [Fact]
    public async Task WatchList_NoRows_PrintsOnlyGlobalDefaults()
    {
        var (exit, stdout, _) = await Run(["watch", "list"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("global: enabled=false concurrency=4 scope=[]");
    }
}
