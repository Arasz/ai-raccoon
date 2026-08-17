using System.CommandLine;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     WP6 (docs/plans/2026-08-16-bank-open-cost-implementation.md §5.2): configuration lives under
///     `settings &lt;subsystem&gt;`, operations stay top level, and the old top-level config verbs are
///     gone rather than aliased.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SettingsCommandTreeTests
{
    /// <summary>The end-state top level: one config noun plus the operations that are not configuration.</summary>
    private static readonly string[] TopLevel = ["settings", "model", "watch", "extract", "encryption", "repair", "serve", "noise"];

    /// <summary>
    ///     Every configuration leaf, with a sample argv that satisfies its arguments. The coverage test
    ///     compares the paths here against the leaves the tree actually exposes, so a settings verb
    ///     added without a row here fails rather than going unchecked.
    /// </summary>
    public static readonly (string Path, string[] Argv)[] SettingsLeaves =
    [
        ("settings access default set", ["settings", "access", "default", "set", "rw"]),
        ("settings access default show", ["settings", "access", "default", "show"]),
        ("settings access set", ["settings", "access", "set", "p1", "ro"]),
        ("settings access unset", ["settings", "access", "unset", "p1"]),
        ("settings access list", ["settings", "access", "list"]),
        ("settings model reset", ["settings", "model", "reset"]),
        ("settings model show", ["settings", "model", "show"]),
        ("settings retrieval alpha set", ["settings", "retrieval", "alpha", "set", "0.5"]),
        ("settings retrieval alpha show", ["settings", "retrieval", "alpha", "show"]),
        ("settings sweep enable", ["settings", "sweep", "enable"]),
        ("settings sweep disable", ["settings", "sweep", "disable"]),
        ("settings sweep interval-hours", ["settings", "sweep", "interval-hours", "12"]),
        ("settings sweep threshold set", ["settings", "sweep", "threshold", "set", "0.3"]),
        ("settings sweep show", ["settings", "sweep", "show"]),
        ("settings noise enable", ["settings", "noise", "enable"]),
        ("settings noise disable", ["settings", "noise", "disable"]),
        ("settings noise show", ["settings", "noise", "show"]),
        ("settings queryguard enable", ["settings", "queryguard", "enable"]),
        ("settings queryguard disable", ["settings", "queryguard", "disable"]),
        ("settings queryguard shadow enable", ["settings", "queryguard", "shadow", "enable"]),
        ("settings queryguard shadow disable", ["settings", "queryguard", "shadow", "disable"]),
        ("settings queryguard structural enable", ["settings", "queryguard", "structural", "enable"]),
        ("settings queryguard structural disable", ["settings", "queryguard", "structural", "disable"]),
        ("settings queryguard structural threshold set", ["settings", "queryguard", "structural", "threshold", "set", "0.9"]),
        ("settings queryguard show", ["settings", "queryguard", "show"]),
        ("settings sync add s3", ["settings", "sync", "add", "s3", "https://example.invalid", "--bucket", "b", "--cli"]),
        ("settings sync add azure", ["settings", "sync", "add", "azure", "c", "--cli", "--account", "a"]),
        ("settings sync remove", ["settings", "sync", "remove"]),
        ("settings sync show", ["settings", "sync", "show"]),
        ("settings ingest scope add", ["settings", "ingest", "scope", "add", "*", "/tmp"]),
        ("settings ingest scope remove", ["settings", "ingest", "scope", "remove", "*", "/tmp"]),
        ("settings ingest scope list", ["settings", "ingest", "scope", "list", "*"]),
        ("settings watch enable", ["settings", "watch", "enable", "*", "true"]),
        ("settings watch disable", ["settings", "watch", "disable", "*", "false"]),
        ("settings watch concurrency", ["settings", "watch", "concurrency", "*", "4"]),
        ("settings watch remove", ["settings", "watch", "remove", "*"]),
        ("settings watch list", ["settings", "watch", "list"]),
        ("settings extract enable", ["settings", "extract", "enable", "true"]),
        ("settings extract mode", ["settings", "extract", "mode", "propose"]),
        ("settings extract interval", ["settings", "extract", "interval", "30"]),
        ("settings extract capacity", ["settings", "extract", "capacity", "100"]),
        ("settings extract exclude add", ["settings", "extract", "exclude", "add", "scratch/"]),
        ("settings extract exclude remove", ["settings", "extract", "exclude", "remove", "scratch/"]),
        ("settings extract exclude list", ["settings", "extract", "exclude", "list"]),
        ("settings extract list", ["settings", "extract", "list"]),
        ("settings maintenance interval", ["settings", "maintenance", "interval", "60"]),
        ("settings maintenance vacuum-interval", ["settings", "maintenance", "vacuum-interval", "7"]),
        ("settings maintenance list", ["settings", "maintenance", "list"]),
        ("settings performance buffer-capacity", ["settings", "performance", "buffer-capacity", "1000"]),
        ("settings performance flush-interval", ["settings", "performance", "flush-interval", "30"]),
        ("settings performance retention", ["settings", "performance", "retention", "28"]),
        ("settings performance list", ["settings", "performance", "list"])
    ];

    /// <summary>
    ///     Leaves outside `settings` that are allowed to write configuration: `model set` re-embeds the
    ///     bank (§5.2) and `encryption` is the bootstrap path (§5.3). `serve` starts a process.
    /// </summary>
    private static readonly string[] WriteOptOuts =
        ["model set local", "model set openai", "encryption bitwarden", "encryption show", "encryption unset", "encryption migrate", "serve", "serve observability"];

    public static TheoryData<string[]> SettingsArgv()
    {
        var data = new TheoryData<string[]>();
        foreach (var (_, argv) in SettingsLeaves)
        {
            data.Add(argv);
        }

        return data;
    }

    /// <summary>The pre-WP6 spelling of each moved verb: the same argv without the `settings` head.</summary>
    public static TheoryData<string[]> RemovedArgv()
    {
        var data = new TheoryData<string[]>();
        foreach (var (_, argv) in SettingsLeaves)
        {
            data.Add([.. argv.Skip(1)]);
        }

        return data;
    }

    [Fact]
    public void Root_ExposesTheEndStateTopLevel()
    {
        var root = CliCommandTree.BuildFullRootCommand();

        root.Children.OfType<Command>().Select(c => c.Name).ShouldBe(TopLevel, ignoreOrder: true);
    }

    /// <summary>
    ///     The verb set used to be a hand-maintained mirror of the tree. It is read off the tree now,
    ///     so a new top-level verb needs no second edit: parsing it must not fall back to the launch root.
    /// </summary>
    [Fact]
    public void NewTopLevelVerb_NeedsNoSecondList()
    {
        CliArgs.TryParse(["settings", "sweep", "show"], out var parsed).ShouldBeTrue();

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "sweep", "show"]);
    }

    /// <summary>The table above must name every settings leaf the tree exposes — no row, no check.</summary>
    [Fact]
    public void SettingsLeaves_CoverEverySettingsPath()
    {
        var settings = CliCommandTree.BuildFullRootCommand().Children.OfType<Command>().Single(c => c.Name == "settings");

        LeafPaths(settings, ["settings"]).ShouldBe(SettingsLeaves.Select(l => l.Path), ignoreOrder: true);
    }

    [Theory]
    [MemberData(nameof(SettingsArgv))]
    public void SettingsPath_Parses(string[] argv)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(argv);

        parseResult.Errors.Select(e => e.Message).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(RemovedArgv))]
    public void OldTopLevelConfigPath_NoLongerParses(string[] argv)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(argv);

        parseResult.Errors.ShouldNotBeEmpty($"'{string.Join(' ', argv)}' still parses");
    }

    /// <summary>The deprecated alias goes with the no-migration ruling (§5.2).</summary>
    [Fact]
    public void WatchScopeAlias_IsGone()
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(["settings", "watch", "scope", "list", "*"]);

        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Theory]
    [InlineData("watch", "registered")]
    [InlineData("extract", "prune")]
    [InlineData("noise", "entries")]
    public void OperationVerb_StaysTopLevel(string family, string verb)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse([family, verb]);

        parseResult.Errors.Select(e => e.Message).ShouldBeEmpty();
        parseResult.CommandResult.Command.Name.ShouldBe(verb);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("openai text-embedding-3-small")]
    public void ModelSet_StaysTopLevel(string tail)
    {
        var parseResult = CliCommandTree.BuildFullRootCommand().Parse(["model", "set", .. tail.Split(' ')]);

        parseResult.Errors.Select(e => e.Message).ShouldBeEmpty();
    }

    /// <summary>
    ///     The gate that tells a split from a rename: every leaf still outside `settings`, minus the
    ///     declared opt-outs, is dispatched and must write no configuration at all.
    /// </summary>
    [Fact]
    public async Task NoConfigurationWriteRemainsReachableOutsideSettings()
    {
        var root = CliCommandTree.BuildFullRootCommand();
        var outside = LeafPaths(root, [])
            .Where(path => !path.StartsWith("settings", StringComparison.Ordinal))
            .Except(WriteOptOuts, StringComparer.Ordinal)
            .ToArray();

        outside.ShouldNotBeEmpty();
        foreach (var path in outside)
        {
            var store = new FakeConfigStore();
            var commands = TestData.CreateConfigCommands(
                store,
                watch: new WatchCommands(new FakeWatchStore()),
                extract: new ExtractCommands(new EmptyOrphanQueueStore()),
                noiseEntries: new NoiseEntriesCommands(NoOpNoiseEntryStore.Instance));

            await CliRun.RunAsync(path.Split(' '), commands);

            store.Settings.ShouldBeEmpty($"'{path}' wrote configuration outside settings");
            store.Configured.ShouldBeNull($"'{path}' reconfigured the embedder outside settings");
        }
    }

    /// <summary>
    ///     Every executable path under the command, as space-joined argv. A command with its own
    ///     action is a path too, even when it also has children (`serve`).
    /// </summary>
    private static IEnumerable<string> LeafPaths(Command command, IReadOnlyList<string> path)
    {
        var children = command.Children.OfType<Command>().ToArray();
        if (path.Count > 0 && (children.Length == 0 || command.Action is not null))
        {
            yield return string.Join(' ', path);
        }

        foreach (var child in children)
        {
            foreach (var leaf in LeafPaths(child, [.. path, child.Name]))
            {
                yield return leaf;
            }
        }
    }

    private sealed class EmptyOrphanQueueStore : IPromotionQueueStore
    {
        public Task<int> PurgeOldDiscardsAsync(long nowUnixSeconds, int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueRow?> ClaimAsync(string projectId, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PruneRejectedAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PromotionQueueOrphanReport> PruneOrphansAsync(bool apply, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromotionQueueOrphanReport(0, new Dictionary<string, int>(StringComparer.Ordinal)));
    }
}
