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
    ///     ADR-0099: folds need an explicit --map; machine ids are fixture data only.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_FormatsTheFoldPlan()
    {
        using var scope = new TempScope();
        var mapPath = scope.WriteFixtureMap();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, ClusterReport(), scope.DataRoot, mapPath);

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
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };
        var mapPath = scope.WriteFixtureMap();

        var stdout = await RunProjectIdsDiagnoseAsync(inner, scope.DataRoot, mapPath);

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
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        await RunProjectIdsAsync(apply: false, inner, scope.DataRoot);

        inner.LastRepairRequest.ShouldBeNull();
    }

    /// <summary>
    ///     Dispatcher row (review MUST-4, never the diagnose gate): --apply commits the project-ids kind.
    ///     Ledger — apply-requests-wrong-kind : --filter ProjectIds_Apply_RequestsTheProjectIdsKind : cluster report.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Apply_RequestsTheProjectIdsKind()
    {
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };
        var mapPath = scope.WriteFixtureMap();

        var stdout = await RunProjectIdsAsync(apply: true, inner, scope.DataRoot, mapPath);

        inner.LastRepairRequest.ShouldBe(RepairKind.ProjectIds);
        inner.LastRepairMapJson.ShouldBe(File.ReadAllText(mapPath));
        stdout.ShouldContain("maintenance poll");
    }

    /// <summary>
    ///     D1 overturns the d-426 keep predicate: NULL-context rows are committed rows
    ///     (project scope, any label) and now fold to the winner with every other committed
    ///     row — so the per-loser line frames the count as moving, never as staying behind.
    ///     The count itself stays visible pre-apply as the verify-they-move instrument.
    ///     Ledger — hide-null-context-counts (overturned by D1) : --filter ProjectIds_DryRun_PrintsPerLoserNullContextCounts : loser with a NULL-ctx row.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_PrintsPerLoserNullContextCounts()
    {
        using var scope = new TempScope();
        var report = new ProjectIdCensusReport(
        [
            new ProjectIdCensusRow("jsaa", false, null, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            new ProjectIdCensusRow("job-search-ai-assistant", false, null, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])
        ], 0, 0, 0, 0, []);
        var mapPath = scope.WriteFixtureMap();

        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, report, scope.DataRoot, mapPath);

        stdout.ShouldContain("1 NULL-context");
        stdout.ShouldContain("fold");
        stdout.ShouldNotContain("stay");
    }

    /// <summary>
    ///     Package A golden test (D2/D6): a pinned-only dry run prints the pinned count on the
    ///     scoreboard, one reason line per pin, and a pinned-only closing summary in D6
    ///     vocabulary — never the converged line, which would hide the waiting pins.
    ///     Ledger — pinned-only-scoreboard : --filter ProjectIds_DryRun_PinnedOnly_PrintsReasonsAndPinnedOnlySummary : shared-only + telemetry-only losers.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_PinnedOnly_PrintsReasonsAndPinnedOnlySummary()
    {
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, PinnedOnlyReport(), scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldContain("3 id(s) censused");
        stdout.ShouldContain("0 fold,");
        stdout.ShouldContain("1 need nothing (already correct or empty)");
        stdout.ShouldContain("2 pinned (waiting with reasons below)");
        stdout.ShouldContain("pinned-shared-only: 'job-search-ai-assistant'");
        stdout.ShouldContain("pinned-telemetry-only: 'AI-RACCOON'");
        stdout.ShouldContain(
            "summary — pinned-only: 0 fold, 0 drop, 0 retire, 0 unresolved, 2 pinned " +
            "(pinned-shared-only: 'job-search-ai-assistant', pinned-telemetry-only: 'AI-RACCOON'), " +
            ProjectIdsRepairCommands.P3ArmedNote + ".");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
    }

    /// <summary>
    ///     Package A: an actionable run with pins extends the repair-needed summary instead of
    ///     hiding the pins behind the fold count — the scoreboard still sums to the censused total.
    ///     F2 restates it in D6 vocabulary (explicit zero counts, inline pin list).
    ///     Ledger — actionable-with-pins : --filter ProjectIds_DryRun_ActionableWithPins_ExtendsRepairNeededSummary : fold + telemetry pin.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_ActionableWithPins_ExtendsRepairNeededSummary()
    {
        using var scope = new TempScope();
        var report = new ProjectIdCensusReport(
        [
            Row("job-search-ai-assistant", entries: 1),
            Row("AI-RACCOON", metricsRows: 1)
        ], 0, 0, 0, 0, []);
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, report, scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldContain("2 id(s) censused");
        stdout.ShouldContain("1 fold,");
        stdout.ShouldContain("1 pinned (waiting with reasons below)");
        stdout.ShouldContain("0 need nothing (already correct or empty)");
        stdout.ShouldContain("pinned-telemetry-only: 'AI-RACCOON'");
        stdout.ShouldContain("summary — repair needed: 1 fold, 0 drop, 0 retire, 0 unresolved, " +
            "1 pinned (pinned-telemetry-only: 'AI-RACCOON') — pass --apply to run the loop until it reports converged.");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
    }

    /// <summary>
    ///     F1 no-blind-request rule overturns the Package A behaviour: --apply on a pinned-only
    ///     plan commits NO request row and reports pinned-only in D6 vocabulary.
    ///     Ledger — apply-with-pins : --filter ProjectIds_Apply_PinnedOnly_CommitsNoRequest_ReportsPinnedOnly : pinned-only report, --apply.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Apply_PinnedOnly_CommitsNoRequest_ReportsPinnedOnly()
    {
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = PinnedOnlyReport() };
        var stdout = await RunProjectIdsAsync(apply: true, inner, scope.DataRoot, scope.WriteFixtureMap());

        inner.LastRepairRequest.ShouldBeNull("a pinned-only first derive commits no blind request row");
        stdout.ShouldContain(
            "summary — pinned-only: 0 fold, 0 drop, 0 retire, 0 unresolved, 2 pinned " +
            "(pinned-shared-only: 'job-search-ai-assistant', pinned-telemetry-only: 'AI-RACCOON'), " +
            ProjectIdsRepairCommands.P3ArmedNote + ".");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
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
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };
        var mapPath = scope.WriteFixtureMap();

        var stdout = await RunProjectIdsAsync(apply: true, inner, scope.DataRoot, mapPath);

        stdout.ShouldContain("quiesce");
        stdout.ShouldContain("re-run");
    }

    /// <summary>ADR-0099 AC2: a map-less dry run plans with the empty map (no folds), writes an editable template beside the bank, and names its path.</summary>
    [Fact]
    public async Task ProjectIds_DryRun_WithoutMap_WritesTemplateAndPlansNoFolds()
    {
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsAsync(apply: false, inner, scope.DataRoot);

        stdout.ShouldNotContain("folds to");
        var templatePath = Path.Combine(scope.DataRoot, ProjectIdsRepairCommands.TemplateFileName);
        stdout.ShouldContain(templatePath);
        File.Exists(templatePath).ShouldBeTrue();
        // The template is a seed, not the empty map: one example alias shape, the bank-wide
        // self-metrics canonical, and this run's unattributed ids pre-filled in Dropped for review.
        var seed = ProjectIdAliasMap.FromJson(File.ReadAllText(templatePath));
        seed.Aliases.ShouldBe([new ProjectIdAliasEntry("old-project-id", "new-project-id")]);
        seed.Canonicals.ShouldBe([MetricsConfigKeys.SelfMetricsProjectId]);
        seed.Dropped.ShouldBe(["jsaa", "job-search-ai-assistant"]);
        inner.LastRepairRequest.ShouldBeNull();
    }

    /// <summary>
    ///     Unattributed ids print as a header plus one id per line — a 40-id comma-joined
    ///     line wraps unreadably. The scoreboard already carries the count.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_PrintsUnresolvedOnePerLine()
    {
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, MixedOutcomeReport(), scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldContain("1 id(s) match no known id — left alone for a human to attribute:");
        var lines = stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
        lines.ShouldContain("  'mystery-guid-0001'");
        lines.Count(line => line.Contains("match no known id")).ShouldBe(1);
    }

    /// <summary>
    ///     Registered ids are already attributed by the live bank: they never ask for a human
    ///     and the template seeds them as canonicals next to the self-metrics sentinel.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_SeedsRegisteredIdsAsCanonicals_NeverUnresolved()
    {
        using var scope = new TempScope();
        var report = new ProjectIdCensusReport(
        [
            Row("my-project", registered: true, entries: 1),
            Row("mystery-guid-0001", entries: 1)
        ], 0, 0, 0, 0, []);
        var inner = new InMemorySettings { ProjectIdsReport = report };

        var stdout = await RunProjectIdsAsync(apply: false, inner, scope.DataRoot);

        stdout.ShouldContain("1 need a human to attribute");
        stdout.ShouldContain("'mystery-guid-0001'");
        stdout.ShouldNotContain("'my-project'");
        var seed = ProjectIdAliasMap.FromJson(File.ReadAllText(Path.Combine(scope.DataRoot, ProjectIdsRepairCommands.TemplateFileName)));
        seed.Canonicals.ShouldBe([MetricsConfigKeys.SelfMetricsProjectId, "my-project"]);
        seed.Dropped.ShouldBe(["mystery-guid-0001"]);
    }

    [Fact]
    public async Task ProjectIds_DryRun_WithoutMap_NeverOverwritesAnExistingTemplate()
    {
        using var scope = new TempScope();
        var templatePath = Path.Combine(scope.DataRoot, ProjectIdsRepairCommands.TemplateFileName);
        Directory.CreateDirectory(scope.DataRoot);
        File.WriteAllText(templatePath, "sentinel-operator-edits");
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsAsync(apply: false, inner, scope.DataRoot);

        File.ReadAllText(templatePath).ShouldBe("sentinel-operator-edits");
        // Portable never-overwrite rule: the pre-existing file routes to the edit-it
        // guidance, never to a "Could not write ... already exists.;" failure line.
        stdout.ShouldContain("Edit the existing template");
        stdout.ShouldNotContain("Could not write the alias-map template");
        stdout.ShouldNotContain(".;");
    }

    [Fact]
    public async Task ProjectIds_DryRun_WithMap_DoesNotWriteATemplate()
    {
        using var scope = new TempScope();
        var mapPath = scope.WriteFixtureMap();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        await RunProjectIdsAsync(apply: false, inner, scope.DataRoot, mapPath);

        File.Exists(Path.Combine(scope.DataRoot, ProjectIdsRepairCommands.TemplateFileName)).ShouldBeFalse();
    }

    [Fact]
    public async Task ProjectIds_Apply_WithoutMap_ForwardsANullMap()
    {
        using var scope = new TempScope();
        // A registered-empty id retires under the empty map, so the plan is actionable and the
        // loop commits (F1 no-blind-request rule: an empty plan would commit nothing).
        var report = new ProjectIdCensusReport(
            [.. ClusterReport().Rows, Row("empty-registered-project", registered: true)], 0, 0, 0, 0, []);
        var inner = new InMemorySettings { ProjectIdsReport = report };

        var stdout = await RunProjectIdsWithExitAsync(apply: true, inner, scope.DataRoot);

        stdout.Exit.ShouldBe(0);
        inner.LastRepairRequest.ShouldBe(RepairKind.ProjectIds);
        inner.LastRepairMapJson.ShouldBeNull();
    }

    [Fact]
    public async Task ProjectIds_WithMissingMap_ReturnsInvalidArgument_AndNeverRequests()
    {
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };
        var missing = Path.Combine(scope.DataRoot, "no-such-map.json");

        var stdout = await RunProjectIdsWithExitAsync(apply: false, inner, scope.DataRoot, missing);

        stdout.Exit.ShouldBe(AiRaccoon.ExitCode.InvalidArgument);
        inner.LastRepairRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ProjectIds_WithMalformedMap_ReturnsInvalidArgument_AndNeverRequests()
    {
        using var scope = new TempScope();
        var bad = Path.Combine(scope.DataRoot, "bad-map.json");
        Directory.CreateDirectory(scope.DataRoot);
        File.WriteAllText(bad, "{ not json");
        var inner = new InMemorySettings { ProjectIdsReport = ClusterReport() };

        var stdout = await RunProjectIdsWithExitAsync(apply: false, inner, scope.DataRoot, bad);

        stdout.Exit.ShouldBe(AiRaccoon.ExitCode.InvalidArgument);
        inner.LastRepairRequest.ShouldBeNull();
    }

    private static ProjectIdCensusReport ClusterReport() => new(
        [
            new ProjectIdCensusRow("jsaa", false, null, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, []),
            new ProjectIdCensusRow("job-search-ai-assistant", false, null, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])
        ], 0, 0, 0, 0, []);

    private sealed class TempScope : IDisposable
    {
        public string DataRoot { get; } = Path.Combine(Path.GetTempPath(), $"repair-test-{Guid.CreateVersion7():N}");

        public string WriteFixtureMap()
        {
            Directory.CreateDirectory(DataRoot);
            var map = new ProjectIdAliasMap(
                [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
                ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
                ["qa-noise-project", "manual-sweep"]);
            var path = Path.Combine(DataRoot, $"map-{Guid.CreateVersion7():N}.json");
            File.WriteAllText(path, map.ToJson(indented: true));
            return path;
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

    /// <summary>
    ///     Pinned-only fixture under the fixture map: jsaa is already canonical (needs nothing),
    ///     job-search-ai-assistant owns only shared-scope entries (pinned-shared-only),
    ///     AI-RACCOON owns only telemetry (pinned-telemetry-only) — zero folds, zero actionable, two pins.
    /// </summary>
    private static ProjectIdCensusReport PinnedOnlyReport() => new(
        [
            Row("jsaa", entries: 1),
            Row("job-search-ai-assistant", sharedEntries: 2),
            Row("AI-RACCOON", metricsRows: 2)
        ], 0, 0, 0, 0, []);

    /// <summary>A census row with every non-id-bearing surface at zero, named arguments only for the fields a fixture needs.</summary>
    private static ProjectIdCensusRow Row(string projectId, bool registered = false, string? registeredName = null,
        long entries = 0, long sharedEntries = 0, long metricsRows = 0, long noiseRows = 0) => new(projectId, registered, registeredName, entries, 0, sharedEntries, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, metricsRows, noiseRows, []);

    /// <summary>
    ///     Ledger — scoreboard-sums-to-total : the five real outcomes (fold/drop/retire/attention/
    ///     nothing) are counted separately and sum to the censused total, replacing the old single
    ///     "would fold" verb that covered all of them regardless of what actually happens.
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_ScoreboardCountsEveryOutcomeSeparately()
    {
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, MixedOutcomeReport(), scope.DataRoot, scope.WriteFixtureMap());

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
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, MixedOutcomeReport(), scope.DataRoot, scope.WriteFixtureMap());

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
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply, diagnose: false, MixedOutcomeReport(), scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldContain("re-running will not clear");
    }

    /// <summary>
    ///     Ledger — leftovers-look-fixable (negative) : the clarifier must not appear when nothing
    ///     needs a human — otherwise it is noise on every converged run instead of a real signal.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Converged_OmitsTheRerunClarifier()
    {
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, ClusterReport(), scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldNotContain("re-running will not clear");    }

    /// <summary>
    ///     f: a dry run ended without a verdict line — a log grep could not tell converged
    ///     from actionable without re-reading the whole report. The last line is always the
    ///     one-line summary, in D6 vocabulary. Dry run with folds/drops/retires waiting:
    ///     repair needed with explicit counts.
    ///     Ledger — missing-closing-summary : MixedOutcome dry run (3 actionable + 1 attention).
    /// </summary>
    [Fact]
    public async Task ProjectIds_DryRun_EndsWithRepairNeededSummary()
    {
        using var scope = new TempScope();
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, MixedOutcomeReport(), scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldContain(
            "summary — repair needed: 1 fold, 1 drop, 1 retire, 1 unresolved, 0 pinned — " +
            "pass --apply to run the loop until it reports converged; 1 id(s) still need a human to attribute.");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
    }

    /// <summary>
    ///     f: the fire-and-forget path — --apply --queue-only queues the request and exits
    ///     without waiting, so a queued request reads as in-progress in D6 vocabulary, never
    ///     as done, until a loop run reports converged.
    ///     Ledger — missing-closing-summary : MixedOutcome --queue-only (3 queued).
    /// </summary>
    [Fact]
    public async Task ProjectIds_QueueOnly_EndsWithRepairInProgressSummary()
    {
        using var scope = new TempScope();
        var inner = new InMemorySettings { ProjectIdsReport = MixedOutcomeReport() };
        var mapPath = scope.WriteFixtureMap();
        CliArgs.TryParse(["repair", "project-ids", "--apply", "--queue-only", "--map", mapPath], out var parsed).ShouldBeTrue();
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();
        var exit = await new ProjectIdsRepairCommands(inner, ProjectIdsRepairCommands.RepairLoopOptions.Test).RunAsync(
            parsed!.ParsedCliArgs, scope.DataRoot,
            new StandardStreams(TextReader.Null, stdoutWriter, stderrWriter), TestContext.Current.CancellationToken);
        var stdout = stdoutWriter.ToString();

        exit.ShouldBe(0);
        inner.LastRepairRequest.ShouldBe(RepairKind.ProjectIds);
        stdout.ShouldContain(
            "summary — repair in progress: 3 change(s) queued for the server — " +
            "the server applies it on its next maintenance poll (~15s).");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
    }

    /// <summary>
    ///     f: the converged run's verdict in D6 vocabulary — explicit zero counts plus the P3
    ///     note — must print too, or the summary's absence reads as a truncated report.
    ///     Ledger — missing-closing-summary : single canonical id, map supplied.
    /// </summary>
    [Fact]
    public async Task ProjectIds_Converged_EndsWithNothingToDoSummary()
    {
        using var scope = new TempScope();
        var report = new ProjectIdCensusReport([Row("jsaa", entries: 1)], 0, 0, 0, 0, []);
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, report, scope.DataRoot, scope.WriteFixtureMap());

        stdout.ShouldContain(
            "summary — converged: 0 fold, 0 drop, 0 retire, 0 unresolved, 0 pinned, " +
            ProjectIdsRepairCommands.P3ArmedNote + ".");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
    }

    /// <summary>
    ///     f: attention-only runs (no folds/drops/retires, but ids a human must place) get the
    ///     D6 attention verdict with explicit zero counts — neither converged nor repair needed.
    ///     Ledger — missing-closing-summary : single unknown id, no map.
    /// </summary>
    [Fact]
    public async Task ProjectIds_AttentionOnly_EndsWithNothingToQueueSummary()
    {
        using var scope = new TempScope();
        var report = new ProjectIdCensusReport([Row("mystery-guid-0001", entries: 1)], 0, 0, 0, 0, []);
        var stdout = await RunProjectIdsAsync(apply: false, diagnose: false, report, scope.DataRoot);

        stdout.ShouldContain(
            "summary — attention needed: 0 fold, 0 drop, 0 retire, 1 unresolved, 0 pinned — " +
            "1 id(s) still need a human to attribute.");
        LastNonEmptyLine(stdout).ShouldStartWith("project-ids repair: summary");
    }

    private static string LastNonEmptyLine(string stdout) =>
        stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).Last();

    private static Task<string> RunProjectIdsAsync(bool apply, bool diagnose, ProjectIdCensusReport report) =>
        RunProjectIdsAsync(apply, diagnose, new InMemorySettings { ProjectIdsReport = report });

    private static Task<string> RunProjectIdsAsync(bool apply, InMemorySettings store) =>
        RunProjectIdsAsync(apply, false, store);

    private static Task<string> RunProjectIdsAsync(bool apply, bool diagnose, ProjectIdCensusReport report, string dataRoot, string? mapPath = null) =>
        RunProjectIdsAsync(apply, diagnose, new InMemorySettings { ProjectIdsReport = report }, dataRoot, mapPath);

    private static Task<string> RunProjectIdsAsync(bool apply, InMemorySettings store, string dataRoot, string? mapPath = null) =>
        RunProjectIdsAsync(apply, false, store, dataRoot, mapPath);

    private static async Task<string> RunProjectIdsDiagnoseAsync(InMemorySettings store) =>
        await RunProjectIdsAsync(false, true, store);

    private static async Task<string> RunProjectIdsDiagnoseAsync(InMemorySettings store, string dataRoot, string? mapPath = null) =>
        await RunProjectIdsAsync(false, true, store, dataRoot, mapPath);

    private static async Task<string> RunProjectIdsAsync(bool apply, bool diagnose, InMemorySettings store)
    {
        using var scope = new TempScope();
        return await RunProjectIdsAsync(apply, diagnose, store, scope.DataRoot);
    }

    private sealed record RunOutcome(string Stdout, int Exit);

    private static async Task<RunOutcome> RunProjectIdsWithExitAsync(bool apply, InMemorySettings store, string dataRoot, string? mapPath = null) =>
        await RunProjectIdsWithExitAsync(apply, false, store, dataRoot, mapPath);

    private static async Task<RunOutcome> RunProjectIdsWithExitAsync(bool apply, bool diagnose, InMemorySettings store, string dataRoot, string? mapPath = null)
    {
        var argv = apply
            ? (mapPath is null ? new[] { "repair", "project-ids", "--apply" } : ["repair", "project-ids", "--apply", "--map", mapPath])
            : diagnose
                ? (mapPath is null ? ["repair", "project-ids", "--diagnose"] : ["repair", "project-ids", "--diagnose", "--map", mapPath])
                : (mapPath is null ? ["repair", "project-ids"] : ["repair", "project-ids", "--map", mapPath]);
        CliArgs.TryParse(argv, out var parsed).ShouldBeTrue();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ProjectIdsRepairCommands(store, ProjectIdsRepairCommands.RepairLoopOptions.Test).RunAsync(parsed!.ParsedCliArgs, dataRoot,
            new StandardStreams(TextReader.Null, stdout, stderr), TestContext.Current.CancellationToken);

        return new RunOutcome(stdout.ToString(), exit);
    }

    private static async Task<string> RunProjectIdsAsync(bool apply, bool diagnose, InMemorySettings store, string dataRoot, string? mapPath = null)
    {
        var outcome = await RunProjectIdsWithExitAsync(apply, diagnose, store, dataRoot, mapPath);
        outcome.Exit.ShouldBe(0);
        return outcome.Stdout;
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
