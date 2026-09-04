using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     `repair reingest`/`repair chunk-index` are thin clients over <see cref="IRepairStore" />
///     (ADR-0075 amendment): the report comes back already computed, and the CLI process never opens
///     the bank — <see cref="InMemorySettings" /> stands in for the acquired server connection, so
///     these run as fast unit tests rather than against a real bank.
///     <para>
///         `repair reingest` discards per-row metadata as a consequence of the re-chunk, not a
///         hazard to work around (owner ruling) — so an operator must see that stated before AND
///         after --apply, never only in one of the two.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RepairCommandsTests
{
    [Fact]
    public async Task Reingest_DryRun_WarnsAboutMetadataLoss()
    {
        var stdout = await RunReingestAsync(apply: false, new ReingestRepairReport(1, 3, 2));

        stdout.ShouldContain("metadata");
        stdout.ShouldContain("rating");
        stdout.ShouldContain("discarded");
    }

    [Fact]
    public async Task Reingest_Apply_WarnsAboutMetadataLoss()
    {
        var stdout = await RunReingestAsync(apply: true, new ReingestRepairReport(1, 3, 2));

        stdout.ShouldContain("metadata");
        stdout.ShouldContain("rating");
        stdout.ShouldContain("discarded");
    }

    /// <summary>
    ///     The three-string fix this task exists for: the old text claimed "with no server running,
    ///     nothing drains it — run memory_embed_pending by hand", which enshrined the CLI-writes-the-
    ///     bank defect as expected behaviour. The server now applies AND drains — there is no manual
    ///     path in the normal case, and the CLI must not advertise one.
    /// </summary>
    [Fact]
    public async Task Reingest_Apply_StatesTheServerAppliesAndDrains_NotAManualFallback()
    {
        var stdout = await RunReingestAsync(apply: true, new ReingestRepairReport(1, 3, 2));

        stdout.ShouldContain("server applies");
        stdout.ShouldContain("maintenance poll");
        stdout.ShouldNotContain("memory_embed_pending");
        stdout.ShouldNotContain("no server running");
    }

    [Fact]
    public async Task Reingest_DryRun_NeverRequestsARepair()
    {
        var inner = new InMemorySettings { ReingestReport = new ReingestRepairReport(1, 3, 2) };

        await RunReingestAsync(apply: false, inner);

        inner.LastRepairRequest.ShouldBeNull();
    }

    [Fact]
    public async Task Reingest_Apply_RequestsTheReingestKind()
    {
        var inner = new InMemorySettings { ReingestReport = new ReingestRepairReport(1, 3, 2) };

        await RunReingestAsync(apply: true, inner);

        inner.LastRepairRequest.ShouldBe(RepairKind.Reingest);
    }

    [Fact]
    public async Task ChunkIndex_Apply_QueuesForTheServer_AndDoesNotClaimToWriteLocally()
    {
        var stdout = await RunChunkIndexAsync(apply: true, new ChunkIndexRepairReport(4, 2, 1));

        stdout.ShouldContain("queued for the server");
        stdout.ShouldContain("maintenance poll");
    }

    [Fact]
    public async Task ChunkIndex_Apply_RequestsTheChunkIndexKind()
    {
        var inner = new InMemorySettings { ChunkIndexReport = new ChunkIndexRepairReport(4, 2, 1) };

        await RunChunkIndexAsync(apply: true, inner);

        inner.LastRepairRequest.ShouldBe(RepairKind.ChunkIndex);
    }

    /// <summary>
    ///     Formatter row (review MUST-4: dispatcher/formatter only, never the diagnose gate — that is
    ///     Diagnose_ListsJsaaCluster alone): dry-run output names the loser, the winner, and the fold.
    ///     Ledger — drop-folds-to-line : --filter ProjectIds_DryRun_FormatsTheFoldPlan : jsaa-cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_FormatsTheFoldPlan()
    {
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, ClusterReport());

        stdout.ShouldContain("job-search-ai-assistant");
        stdout.ShouldContain("jsaa");
        stdout.ShouldContain("folds to");
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): --diagnose reports without committing a request.
    ///     Ledger — diagnose-requests-anyway : --filter ProjectIds_DiagnoseFlag_ReportsWithoutRequesting : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DiagnoseFlag_ReportsWithoutRequesting()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsDiagnoseAsync(inner);

        stdout.ShouldContain("job-search-ai-assistant");
        inner.LastRepairRequest.ShouldBeNull();
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): dry run never commits.
    ///     Ledger — dry-run-requests : --filter ProjectIds_DryRun_NeverRequestsARepair : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_NeverRequestsARepair()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        await RunProjectIdsAsync(apply: false, inner);

        inner.LastRepairRequest.ShouldBeNull();
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): --apply commits the project-ids kind.
    ///     Ledger — apply-requests-wrong-kind : --filter ProjectIds_Apply_RequestsTheProjectIdsKind : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Apply_RequestsTheProjectIdsKind()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsAsync(apply: true, inner);

        inner.LastRepairRequest.ShouldBe(RepairKind.ProjectIds);
        stdout.ShouldContain("maintenance poll");
    }

    /// <summary>
    ///     d-426 SHOULD-2: the diagnose names each loser's NULL-context rows — the keep predicate
    ///     deliberately leaves them behind, so the operator must see (and verify) them before
    ///     --apply instead of discovering permanent orphans afterwards.
    ///     Ledger — hide-null-context-counts : --filter ProjectIds_DryRun_PrintsPerLoserNullContextCounts : loser with a NULL-ctx row.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_PrintsPerLoserNullContextCounts()
    {
        var report = new ProjectIdCensusReport(
        [
            new ProjectIdCensusRow("jsaa", false, null, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            new ProjectIdCensusRow("job-search-ai-assistant", false, null, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])
        ], 0, 0, 0, 0, []);

        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, report);

        stdout.ShouldContain("1 NULL-context");
    }

    /// <summary>
    ///     d-426 SHOULD-5: the fold is single-pass — a write under a folded id during the apply
    ///     window re-creates the loser key behind the plan's back, so the operator must quiesce
    ///     writers or loop diagnose→apply until no folds remain. The --apply receipt states that.
    ///     Ledger — drop-quiesce-line : --filter ProjectIds_Apply_NamesQuiesceOrRerun : cluster report, --apply.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Apply_NamesQuiesceOrRerun()
    {
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsAsync(apply: true, inner);

        stdout.ShouldContain("quiesce");
        stdout.ShouldContain("re-run");
    }

    private static ProjectIdCensusReport ClusterReport() => new(
        [
            new ProjectIdCensusRow("jsaa", false, null, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            new ProjectIdCensusRow("job-search-ai-assistant", false, null, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])
        ], 0, 0, 0, 0, []);

    /// <summary>
    ///     One row of each of the five real outcomes FromCensus produces: a fold (job-search-ai-assistant
    ///     -&gt; jsaa), a drop (qa-noise-project, owns deletable content), a retire (registered, owns
    ///     nothing), an id needing human attribution (unknown id, owns content), and an id already
    ///     canonical under the map (ai-badger — needs nothing).
    /// </summary>
    private static ProjectIdCensusReport MixedOutcomeReport() => new(
        [
            Row("job-search-ai-assistant", entries: 1),
            Row("qa-noise-project", entries: 1),
            Row("empty-registered-project", registered: true),
            Row("mystery-guid-0001", entries: 1),
            Row("ai-badger", entries: 1)
        ], 0, 0, 0, 0, []);

    /// <summary>A census row with every non-id-bearing surface at zero, named arguments only for the fields a fixture needs.</summary>
    private static ProjectIdCensusRow Row(string projectId, bool registered = false, string? registeredName = null,
        long entries = 0) => new(projectId, registered, registeredName, entries, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    /// <summary>
    ///     Ledger — scoreboard-sums-to-total : the five real outcomes (fold/drop/retire/attention/
    ///     nothing) are counted separately and sum to the censused total, replacing the old single
    ///     "would fold" verb that covered all of them regardless of what actually happens.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_ScoreboardCountsEveryOutcomeSeparately()
    {
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, MixedOutcomeReport());

        stdout.ShouldContain("5 id(s) censused");
        stdout.ShouldContain("1 fold,");
        stdout.ShouldContain("1 drop (test residue)");
        stdout.ShouldContain("1 retire (registered, empty)");
        stdout.ShouldContain("1 need a human to attribute");
        stdout.ShouldContain("1 need nothing (already correct or empty)");
    }

    /// <summary>
    ///     Ledger — retired-projects-invisible : the server's FoldProjectsAsync deletes every
    ///     RetiredProjects row (ProjectIdsRepair.cs), but the CLI never printed anything about them —
    ///     an operator watching only stdout had no way to know those ids were touched at all.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_NamesEachRetiredProject()
    {
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, MixedOutcomeReport());

        stdout.ShouldContain("empty-registered-project");
        stdout.ShouldContain("retires");
    }

    /// <summary>
    ///     Ledger — leftovers-look-fixable : the only existing re-run guidance (quiesce-or-rerun)
    ///     is about a concurrent-write hazard during --apply, not about ids the alias map cannot
    ///     place. Without this line, repeatedly running 'repair project-ids' looks like it should
    ///     eventually converge on ids that in fact never will until a human or the map changes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectIds_IdNeedingAttention_StatesRerunningWontClearIt(bool apply)
    {
        var stdout = await RunProjectIdsAsync(apply, diagnose: false, MixedOutcomeReport());

        stdout.ShouldContain("re-running will not clear");
    }

    /// <summary>
    ///     Ledger — leftovers-look-fixable (negative) : the clarifier must not appear when nothing
    ///     needs a human — otherwise it is noise on every converged run instead of a real signal.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Converged_OmitsTheRerunClarifier()
    {
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, ClusterReport());

        stdout.ShouldNotContain("re-running will not clear");
    }

    private static Task<string> RunProjectIdsAsync(bool apply, bool diagnose, ProjectIdCensusReport report) =>
        RunProjectIdsAsync(apply, diagnose, new InMemorySettings { ProjectIdsReport = report });

    private static Task<string> RunProjectIdsAsync(bool apply, InMemorySettings store) =>
        RunProjectIdsAsync(apply, false, store);

    private static async Task<string> RunProjectIdsDiagnoseAsync(InMemorySettings store) =>
        await RunProjectIdsAsync(false, true, store);

    private static async Task<string> RunProjectIdsAsync(bool apply, bool diagnose, InMemorySettings store)
    {
        var argv = apply
            ? new[] { "repair", "project-ids", "--apply" }
            : diagnose
                ? ["repair", "project-ids", "--diagnose"]
                : ["repair", "project-ids"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ProjectIdsRepairCommands(store).RunAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }

    [Fact]
    public async Task ChunkIndex_DryRun_NeverRequestsARepair()
    {
        var inner = new InMemorySettings { ChunkIndexReport = new ChunkIndexRepairReport(4, 2, 1) };

        await RunChunkIndexAsync(apply: false, inner);

        inner.LastRepairRequest.ShouldBeNull();
    }

    private static Task<string> RunReingestAsync(bool apply, ReingestRepairReport report) => RunReingestAsync(apply, new InMemorySettings { ReingestReport = report });

    private static async Task<string> RunReingestAsync(bool apply, InMemorySettings store)
    {
        var argv = apply ? new[] { "repair", "reingest", "--apply" } : ["repair", "reingest"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ReingestRepairCommands(store).RunAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }

    private static Task<string> RunChunkIndexAsync(bool apply, ChunkIndexRepairReport report) => RunChunkIndexAsync(apply, new InMemorySettings { ChunkIndexReport = report });

    private static async Task<string> RunChunkIndexAsync(bool apply, InMemorySettings store)
    {
        var argv = apply ? new[] { "repair", "chunk-index", "--apply" } : ["repair", "chunk-index"];
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ChunkIndexRepairCommands(store).RunAsync(parsed!.ParsedCliArgs,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        exit.ShouldBe(0, $"stderr: {stderr}");
        return stdout.ToString();
    }
}
