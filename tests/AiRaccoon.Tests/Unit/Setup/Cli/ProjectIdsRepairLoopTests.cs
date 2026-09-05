using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Package F loop state-machine tests (D5): `repair project-ids --apply` is derive→commit-
///     request→poll→reap→re-derive until done. The fake <see cref="IRepairStore" /> serves scripted
///     census sequences (the stand-in for the server applying requests between polls); the loop
///     never opens the bank itself — ADR-0075. All runs use zero-delay loop options so no test
///     ever sleeps for the ~15s production poll interval.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairLoopTests
{
    /// <summary>
    ///     F AC(1): one invocation converges a quiesced multi-pass bank — two folds drain over two
    ///     passes (proving the loop re-derives instead of single-shotting), then the D6 converged
    ///     line closes the run.
    /// </summary>
    [Fact]
    public async Task Loop_QuiescedMultiPass_Converges()
    {
        using var scope = new TempScope();
        var store = new SequenceRepairStore(
        [
            Report(("a", 2), ("b", 2), ("w", 0)),
            Report(("b", 2), ("w", 2)),
            Report(("w", 4))
        ]);

        var stdout = await RunLoopAsync(["repair", "project-ids", "--apply", "--map", scope.MapPath], store, scope.DataRoot);

        store.RequestCalls.ShouldBe(2, "two passes each commit one request; the converged re-derive commits none");
        stdout.ShouldContain("pass 1");
        stdout.ShouldContain("pass 2");
        stdout.ShouldContain("moved");
        LastNonEmptyLine(stdout).ShouldBe(
            "project-ids repair: summary — converged: 0 fold, 0 drop, 0 retire, 0 unresolved, 0 pinned, " +
            P3Armed + ".");
    }

    /// <summary>
    ///     F AC(2): a live writer keeps census totals growing — the loop runs to its pass bound,
    ///     reports writers-active with quiesce guidance, and never claims a false converged.
    /// </summary>
    [Fact]
    public async Task Loop_LiveWriter_EndsWritersActiveWithQuiesceGuidance()
    {
        using var scope = new TempScope();
        var store = new SequenceRepairStore(
        [
            Report(("a", 2), ("w", 0)),
            Report(("a", 2), ("w", 2)),
            Report(("a", 2), ("w", 4)),
            Report(("a", 2), ("w", 6)),
            Report(("a", 2), ("w", 8))
        ]);

        var stdout = await RunLoopAsync(["repair", "project-ids", "--apply", "--map", scope.MapPath], store, scope.DataRoot);

        store.RequestCalls.ShouldBe(
            ProjectIdsRepairCommands.RepairLoopOptions.Test.MaxPasses,
            "writers-active loops to the pass bound instead of converging or going stuck");
        stdout.ShouldContain("writers-active");
        stdout.ShouldContain("quiesce");
        stdout.ShouldNotContain("converged");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary — writers-active:");
    }

    /// <summary>
    ///     F1 no-blind-request rule (review #614): the loop re-derives FIRST, so a pinned-only
    ///     plan reports pinned-only without committing a (possibly empty) repair_requests row.
    /// </summary>
    [Fact]
    public async Task Loop_PinnedOnly_CommitsNoRequest_ReportsPinnedOnly()
    {
        using var scope = new TempScope();
        var store = new SequenceRepairStore([PinnedOnlyReport()]);

        var stdout = await RunLoopAsync(["repair", "project-ids", "--apply", "--map", scope.MapPath], store, scope.DataRoot);

        store.RequestCalls.ShouldBe(0, "a pinned-only first derive commits nothing");
        store.ReportCalls.ShouldBe(1, "nothing to request means nothing to poll — one derive, then the verdict");
        LastNonEmptyLine(stdout).ShouldBe(
            "project-ids repair: summary — pinned-only: 0 fold, 0 drop, 0 retire, 0 unresolved, " +
            "2 pinned (pinned-shared-only: 'a', pinned-telemetry-only: 't'), " +
            P3Armed + ".");
    }

    /// <summary>
    ///     F1 stuck class: an identical actionable set across 2 passes with zero rows moved aborts
    ///     with a diagnosis naming the stuck ids — and commits no second request onto the pile.
    /// </summary>
    [Fact]
    public async Task Loop_IdenticalActionableZeroMoved_AbortsStuck_WithDiagnosis()
    {
        using var scope = new TempScope();
        var store = new SequenceRepairStore([Report(("a", 2), ("w", 0))]);

        var stdout = await RunLoopAsync(["repair", "project-ids", "--apply", "--map", scope.MapPath], store, scope.DataRoot);

        store.RequestCalls.ShouldBe(1, "the stuck verdict fires before a second blind request piles on");
        stdout.ShouldContain("stuck");
        stdout.ShouldContain("'a'");
        stdout.ShouldContain("zero rows moved");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary — stuck:");
    }

    /// <summary>
    ///     F AC(3) / ADR-0075: the loop issues requests plus reads only — it constructs against a
    ///     fake implementing exactly <see cref="IRepairStore" /> (no bank surface exists to write),
    ///     and its constructor takes nothing that could open the bank from the CLI process.
    /// </summary>
    [Fact]
    public async Task Loop_UsesOnlyRepairStoreReadsAndRequests()
    {
        using var scope = new TempScope();
        SequenceRepairStore store = new([Report(("w", 1))]);

        await RunLoopAsync(["repair", "project-ids", "--apply", "--map", scope.MapPath], store, scope.DataRoot);

        store.ReportCalls.ShouldBeGreaterThan(0);
        typeof(ProjectIdsRepairCommands).GetConstructors().ShouldHaveSingleItem();
        foreach (var parameter in typeof(ProjectIdsRepairCommands).GetConstructors()[0].GetParameters())
        {
            (parameter.IsOptional
                || parameter.ParameterType == typeof(IRepairStore)
                || parameter.ParameterType == typeof(ProjectIdsRepairCommands.RepairLoopOptions)
                || parameter.ParameterType == typeof(TimeProvider)).ShouldBeTrue(
                $"CLI commands stay read-and-request-only: unexpected constructor dependency {parameter.ParameterType.Name}");
        }
    }

    /// <summary>
    ///     F1 escape hatch: --queue-only preserves fire-and-forget — one derive, one request,
    ///     no reaping, and the in-progress D6 line.
    /// </summary>
    [Fact]
    public async Task QueueOnly_CommitsOnceAndExitsWithoutReaping()
    {
        using var scope = new TempScope();
        var store = new SequenceRepairStore([Report(("a", 2), ("w", 0))]);

        var stdout = await RunLoopAsync(
            ["repair", "project-ids", "--apply", "--queue-only", "--map", scope.MapPath], store, scope.DataRoot);

        store.RequestCalls.ShouldBe(1);
        store.ReportCalls.ShouldBe(1, "queue-only never re-derives — it queues and exits");
        stdout.ShouldNotContain("pass 1");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary — repair in progress:");
    }

    /// <summary>F1 + no-blind-request: --queue-only on a pinned-only plan still commits nothing.</summary>
    [Fact]
    public async Task QueueOnly_PinnedOnly_CommitsNoRequest()
    {
        using var scope = new TempScope();
        var store = new SequenceRepairStore([PinnedOnlyReport()]);

        var stdout = await RunLoopAsync(
            ["repair", "project-ids", "--apply", "--queue-only", "--map", scope.MapPath], store, scope.DataRoot);

        store.RequestCalls.ShouldBe(0);
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary — pinned-only:");
    }

    /// <summary>
    ///     The F1 bound, pinned by reasoning: one server maintenance poll is ~15s (H7), so each
    ///     pass waits one poll; ≤10 passes cap the repair_requests rows at ten and the wall clock
    ///     near three minutes, with a 10-minute total backstop for loaded machines.
    /// </summary>
    [Fact]
    public void LoopOptions_Defaults_BoundTheLoop()
    {
        var defaults = ProjectIdsRepairCommands.RepairLoopOptions.Default;

        defaults.MaxPasses.ShouldBe(10);
        defaults.PollInterval.ShouldBe(TimeSpan.FromSeconds(15));
        defaults.TotalBudget.ShouldBe(TimeSpan.FromMinutes(10));
    }

    /// <summary>
    ///     A census row owning project-scope entries under the test map's aliases: the alias
    ///     target 'w' is canonical (needs nothing), every other id folds to it.
    /// </summary>
    /// <summary>
    ///     The durable map an applied bank holds under the test map ('a' and 'b' fold to 'w'):
    ///     every fake census carries it, so the D6 (iv) clause reads real rows instead of a
    ///     constant — the same two aliases <see cref="TempScope" /> writes to disk.
    /// </summary>
    private static readonly ProjectIdAliasEntry[] DurableAliases =
        [new ProjectIdAliasEntry("a", "w"), new ProjectIdAliasEntry("b", "w")];

    /// <summary>The P3 clause those two alias rows produce, spelled out where the verdicts assert it.</summary>
    private const string P3Armed = "P3 armed (2 alias, 0 dropped)";

    private static ProjectIdCensusReport Report(params (string Id, long Entries)[] rows) => new(
        rows.Select(entry => new ProjectIdCensusRow(
            entry.Id, false, null, entry.Entries, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])).ToList(),
        0, 0, 0, 0, [], DurableAliases, []);

    /// <summary>
    ///     Pinned-only under the test map: 'w' canonical, 'a' shared-only, 't' telemetry-only —
    ///     zero actionable, two pins.
    /// </summary>
    private static ProjectIdCensusReport PinnedOnlyReport() => new(
    [
        new ProjectIdCensusRow("w", false, null, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
        new ProjectIdCensusRow("a", false, null, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
        new ProjectIdCensusRow("t", false, null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, [])
    ], 0, 0, 0, 0, [], DurableAliases, []);

    private static string LastNonEmptyLine(string stdout) =>
        stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).Last();

    private static async Task<string> RunLoopAsync(string[] argv, IRepairStore store, string dataRoot)
    {
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ProjectIdsRepairCommands(
                store, ProjectIdsRepairCommands.RepairLoopOptions.Test, TimeProvider.System)
            .RunAsync(parsed!.ParsedCliArgs, dataRoot,
                new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);
        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }

    /// <summary>
    ///     An <see cref="IRepairStore" /> scripted with census snapshots: each report call serves
    ///     the next snapshot (the last repeats — the server at rest), each request call is counted.
    ///     Implements exactly <see cref="IRepairStore" /> — the ADR-0075 half of the contract.
    /// </summary>
    private sealed class SequenceRepairStore(IReadOnlyList<ProjectIdCensusReport> snapshots) : IRepairStore
    {
        private int _served;

        public int ReportCalls { get; private set; }

        public int RequestCalls { get; private set; }

        public string? LastMapJson { get; private set; }

        public Task<ProjectIdCensusReport> ReportProjectIdsAsync(CancellationToken cancellationToken = default)
        {
            ReportCalls++;
            var snapshot = snapshots[Math.Min(_served, snapshots.Count - 1)];
            _served++;
            return Task.FromResult(snapshot);
        }

        public Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default, string? projectIdsMapJson = null)
        {
            kind.ShouldBe(RepairKind.ProjectIds);
            RequestCalls++;
            LastMapJson = projectIdsMapJson;
            return Task.CompletedTask;
        }

        public Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("project-ids loop tests never ask for the reingest report");

        public Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("project-ids loop tests never ask for the chunk-index report");
    }

    private sealed class TempScope : IDisposable
    {
        public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), $"repair-loop-test-{Guid.CreateVersion7():N}");

        public string MapPath { get; }

        public TempScope()
        {
            Directory.CreateDirectory(DataRoot);
            // 'w' is canonical; 'a' and 'b' fold to it. 't' is unmapped on purpose: owning
            // only telemetry it pins telemetry-only instead of going unresolved (D3).
            var map = new ProjectIdAliasMap(
                [new ProjectIdAliasEntry("a", "w"), new ProjectIdAliasEntry("b", "w")],
                ["w"],
                []);
            MapPath = Path.Combine(DataRoot, "map.json");
            File.WriteAllText(MapPath, map.ToJson(indented: true));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(DataRoot))
                {
                    Directory.Delete(DataRoot, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
