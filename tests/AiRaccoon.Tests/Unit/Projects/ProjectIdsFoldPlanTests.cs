using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     Air-merge P2: the repair folds loser ids into their canonical winners (and deletes drop
///     candidates) from a plan derived from the census plus an explicit alias map — never
///     from hand-written id lists at the repair site, so pull-time fold and the ToolGate fold keep
///     consuming the same table.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsFoldPlanTests
{
    // Key-mapping row, not a fold row: pins the outbox kind string the CLI and the job agree on.
    // Ledger — kind-key-drifts : --filter ProjectIds_RepairKind_MapsToTheProjectIdsOutboxKey : enum, no fixture.
    [Fact]
    public void ProjectIds_RepairKind_MapsToTheProjectIdsOutboxKey()
    {
        RepairKind.ProjectIds.ToKey().ShouldBe("project-ids");
        RepairKinds.ProjectIds.ShouldBe("project-ids");
    }

    // Ledger — unknown-loser-not-folded : --filter FromCensus_MapsKnownLosersToTheirWinners : jsaa 2+queued, loser 1+queued.
    [Fact]
    public void FromCensus_MapsKnownLosersToTheirWinners()
    {
        var report = Report(
            Row("jsaa", projectEntries: 2, queued: 2),
            Row("job-search-ai-assistant", projectEntries: 1, queued: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBe([new ProjectIdFold("job-search-ai-assistant", "jsaa")]);
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBeEmpty();
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — guid-falls-to-unresolved : --filter FromCensus_FoldsAGuidLoser_ByItsProjectsRowName : guid + registered pre-guid name.
    [Fact]
    public void FromCensus_FoldsAGuidLoser_ByItsProjectsRowName()
    {
        // The live 01a062f4 guid is registered under its pre-guid name; the alias map never
        // hardcodes the guid itself (P1 decision), so the plan resolves it through that name.
        var guid = "01a062f4-0000-7000-8000-000000000001";
        var report = Report(Row(guid, projectEntries: 2, registered: true, registeredName: "job-search-ai-assistant"));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBe([new ProjectIdFold(guid, "jsaa")]);
    }

    // Ledger — case-folds-to-itself : --filter FromCensus_FoldsCaseSplitLosers_IntoTheLowercaseWinner : UPPER + lowercase pair.
    [Fact]
    public void FromCensus_FoldsCaseSplitLosers_IntoTheLowercaseWinner()
    {
        var report = Report(
            Row("ai-raccoon", projectEntries: 1),
            Row("AI-RACCOON", projectEntries: 1, settingsKeys: ["ingest.scope.AI-RACCOON"]));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBe([new ProjectIdFold("AI-RACCOON", "ai-raccoon")]);
    }

    // Ledger — dropped-ids-folded : --filter FromCensus_DeletesDroppedIds_InsteadOfFoldingThem : qa-noise + manual-sweep rows.
    [Fact]
    public void FromCensus_DeletesDroppedIds_InsteadOfFoldingThem()
    {
        var report = Report(
            Row("qa-noise-project", projectEntries: 1),
            Row("manual-sweep", projectEntries: 1, watches: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBe(["qa-noise-project", "manual-sweep"], "the plan preserves census order");
    }

    // Ledger — zero-row-guid-kept : --filter FromCensus_RetiresZeroEntryProjectsRows_WithNoAttachments : registered zero-row guid.
    [Fact]
    public void FromCensus_RetiresZeroEntryProjectsRows_WithNoAttachments()
    {
        var guid = "01a03024-0000-7000-8000-000000000001";
        var report = Report(Row(guid, registered: true, registeredName: "ai-badger"));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBe([guid]);
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — typo-folded-or-dropped : --filter FromCensus_LeavesTrueTyposUnresolved_NeverFolded : unattributable jsaaa row.
    [Fact]
    public void FromCensus_LeavesTrueTyposUnresolved_NeverFolded()
    {
        // A typo is nobody's alias and nobody's name: P3 refuses its future writes, but P2 must
        // not move or delete rows it cannot attribute.
        var report = Report(Row("jsaaa", projectEntries: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBeEmpty();
        plan.Unresolved.ShouldBe(["jsaaa"]);
    }

    // Ledger — canonical-self-folds : --filter FromCensus_CanonicalSelf_WithEntries_IsANoOp : winner-only bank.
    [Fact]
    public void FromCensus_CanonicalSelf_WithEntries_IsANoOp()
    {
        var report = Report(Row("jsaa", projectEntries: 2, queued: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.IsEmpty.ShouldBeTrue();
    }

    // Ledger — registered-is-canonical : --filter FromCensus_RegisteredIdWithEntries_IsCanonical_NeverUnresolved : registered + entries, empty map.
    [Fact]
    public void FromCensus_RegisteredIdWithEntries_IsCanonical_NeverUnresolved()
    {
        // Registered means the live projects table already attributes the id — its own
        // canonical whether or not any map lists it, so it never needs a human.
        var report = Report(Row("my-project", projectEntries: 2, registered: true, registeredName: "my-project"));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Empty);

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBeEmpty();
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — self-metrics-is-canonical : --filter FromCensus_SelfMetricsSentinel_IsCanonical_WithoutAnyMapEntry : sentinel + content, empty map.
    [Fact]
    public void FromCensus_SelfMetricsSentinel_IsCanonical_WithoutAnyMapEntry()
    {
        // The bank-wide sentinel is a real id on every deployment, not machine-local
        // attribution — no map entry needed to place it.
        var report = Report(Row(MetricsConfigKeys.SelfMetricsProjectId, qualityRows: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Empty);

        plan.IsEmpty.ShouldBeTrue();
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — empty-bank-plans-work : --filter FromCensus_OnACleanBank_IsEmpty : zero rows.
    [Fact]
    public void FromCensus_OnACleanBank_IsEmpty()
    {
        var plan = ProjectIdsFoldPlan.FromCensus(
            new ProjectIdCensusReport([], 0, 0, 0, 0, []), FixtureMap());

        plan.IsEmpty.ShouldBeTrue();
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — metrics-only-dropped-replanned : --filter FromCensus_DroppedIdWithOnlyMetrics_IsNotScheduled : Row(metricsRows: 1).
    [Fact]
    public void FromCensus_DroppedIdWithOnlyMetrics_IsNotScheduled()
    {
        var report = Report(Row("qa-noise-project", metricsRows: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Dropped.ShouldBeEmpty("metrics are never touched — scheduling this re-plans forever as a no-op");
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — quality-rows-dropped-skipped : --filter FromCensus_DroppedIdWithQualityRows_IsScheduled : Row(qualityRows: 1).
    [Fact]
    public void FromCensus_DroppedIdWithQualityRows_IsScheduled()
    {
        var report = Report(Row("manual-sweep", qualityRows: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Dropped.ShouldBe(["manual-sweep"]);
    }

    // Ledger — noise-only-dropped-replanned : --filter FromCensus_DroppedIdWithOnlyNoise_IsNotScheduled : Row(noiseRows: 1).
    [Fact]
    public void FromCensus_DroppedIdWithOnlyNoise_IsNotScheduled()
    {
        var report = Report(Row("qa-noise-project", noiseRows: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Dropped.ShouldBeEmpty("noise rows are never touched — same no-op re-plan as metrics");
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — discards-only-dropped-skipped : --filter FromCensus_DroppedIdWithDiscards_IsScheduled : Row(discards: 1).
    [Fact]
    public void FromCensus_DroppedIdWithDiscards_IsScheduled()
    {
        var report = Report(Row("manual-sweep", discards: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Dropped.ShouldBe(["manual-sweep"], "discards follow the fold — the gate must not over-exclude");
    }

    // Ledger — committed-folds-null-only : --filter FromCensus_NullOnlyProjectLoser_PlansFold : alias loser, project rows all NULL-context.
    [Fact]
    public void FromCensus_NullOnlyProjectLoser_PlansFold()
    {
        // D1 overturns the d-426 keep: NULL-context project rows ARE committed rows
        // (ProjectRows.Scopes), so a loser owning only them still plans a fold — the
        // broadened applier (Package B) moves them; zero-move is impossible by construction.
        var report = Report(Row("job-search-ai-assistant", projectEntries: 2, nullContextEntries: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBe([new ProjectIdFold("job-search-ai-assistant", "jsaa")]);
    }

    // Ledger — committed-folds-custom : --filter FromCensus_CustomLabeledLoser_PlansFold : alias loser, custom-scope rows.
    [Fact]
    public void FromCensus_CustomLabeledLoser_PlansFold()
    {
        var report = Report(Row("job-search-ai-assistant", customEntries: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBe([new ProjectIdFold("job-search-ai-assistant", "jsaa")]);
    }

    // Ledger — shared-never-folds : --filter FromCensus_SharedOnlyLoser_NeverFolds : alias loser, shared-scope rows only.
    [Fact]
    public void FromCensus_SharedOnlyLoser_NeverFolds()
    {
        // Corrected D1: shared rows are cross-project by design (global (path,hash) bucket)
        // and are NEVER folded — a shared-keyed-only loser plans no fold (Package A pins it
        // with a reason; asserting the no-fold half here on the existing API).
        var report = Report(Row("job-search-ai-assistant", sharedEntries: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
    }

    // Ledger — shared-pinned : --filter FromCensus_SharedOnlyLoser_PinsSharedOnlyWithReason : alias loser, shared rows only.
    [Fact]
    public void FromCensus_SharedOnlyLoser_PinsSharedOnlyWithReason()
    {
        var report = Report(Row("job-search-ai-assistant", sharedEntries: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        var pin = plan.Pinned.ShouldHaveSingleItem();
        pin.ProjectId.ShouldBe("job-search-ai-assistant");
        pin.Bucket.ShouldBe(ProjectIdsFoldPlan.PinnedSharedOnly);
        pin.Reason.ShouldContain("2 shared-scope");
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — telemetry-pinned : --filter FromCensus_TelemetryOnlyLoser_PinsTelemetryOnlyWithReason : alias loser, metrics only.
    [Fact]
    public void FromCensus_TelemetryOnlyLoser_PinsTelemetryOnlyWithReason()
    {
        // Telemetry is regenerable derived data the repair never touches: an attributed id
        // owning only it waits with a reason instead of vanishing into a silent continue.
        var report = Report(Row("job-search-ai-assistant", metricsRows: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        var pin = plan.Pinned.ShouldHaveSingleItem();
        pin.ProjectId.ShouldBe("job-search-ai-assistant");
        pin.Bucket.ShouldBe(ProjectIdsFoldPlan.PinnedTelemetryOnly);
        pin.Reason.ShouldContain("telemetry");
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — workspaces-pinned : --filter FromCensus_WorkspaceOnlyLoser_PinsOpenWorkspacesWithReason : alias loser, workspaces only.
    [Fact]
    public void FromCensus_WorkspaceOnlyLoser_PinsOpenWorkspacesWithReason()
    {
        // Workspaces never move across projects (isolation invariant): an otherwise-foldable
        // loser with open workspaces pins with a reason instead of folding or vanishing.
        var report = Report(Row("AI-RACCOON", workspaces: 1, workspaceEntries: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        var pin = plan.Pinned.ShouldHaveSingleItem();
        pin.ProjectId.ShouldBe("AI-RACCOON");
        pin.Bucket.ShouldBe(ProjectIdsFoldPlan.PinnedOpenWorkspaces);
        pin.Reason.ShouldContain("workspace");
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — attributed-never-silent : --filter FromCensus_EmptyAliasLoser_PinsGenericInsteadOfSilentlyContinuing : alias loser, zero everywhere.
    [Fact]
    public void FromCensus_EmptyAliasLoser_PinsGenericInsteadOfSilentlyContinuing()
    {
        // D2's core honesty rule: a map-attributed id with zero executable rows lands in a
        // pinned bucket with a reason line — never a silent continue that hides it.
        var report = Report(Row("job-search-ai-assistant"));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        var pin = plan.Pinned.ShouldHaveSingleItem();
        pin.ProjectId.ShouldBe("job-search-ai-assistant");
        pin.Bucket.ShouldBe(ProjectIdsFoldPlan.PinnedNoMoveableContent);
        plan.Unresolved.ShouldBeEmpty();
    }

    // Ledger — attributed-never-silent : --filter FromCensus_GuidLoserByName_WithOnlyTelemetry_PinsInsteadOfSilentlyContinuing : guid via registered name.
    [Fact]
    public void FromCensus_GuidLoserByName_WithOnlyTelemetry_PinsInsteadOfSilentlyContinuing()
    {
        // The registered-name fold branch pins by the same rule: attribution without
        // executable rows is waiting work with a reason, not an invisible skip.
        var guid = "01a062f4-0000-7000-8000-000000000001";
        var report = Report(Row(guid, registered: true, registeredName: "job-search-ai-assistant", metricsRows: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        var pin = plan.Pinned.ShouldHaveSingleItem();
        pin.ProjectId.ShouldBe(guid);
        pin.Bucket.ShouldBe(ProjectIdsFoldPlan.PinnedTelemetryOnly);
    }

    // Ledger — canonical-self-silent : --filter FromCensus_CanonicalSelf_WithEntries_PlansNeitherFoldNorPin : winner-only bank.
    [Fact]
    public void FromCensus_CanonicalSelf_WithEntries_PlansNeitherFoldNorPin()
    {
        // A canonical id resolving to itself is already placed — pinning it would turn every
        // converged bank into a pinned-only report, so the self-branch stays silent by design.
        var report = Report(Row("jsaa", projectEntries: 2, queued: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, FixtureMap());

        plan.Folds.ShouldBeEmpty();
        plan.Pinned.ShouldBeEmpty();
        plan.IsEmpty.ShouldBeTrue();
    }

    // Explicit fixture: machine ids as TEST DATA (allowed). Production Default is empty (ADR-0099).
    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
        ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
        ["qa-noise-project", "manual-sweep"]);

    [Fact]
    public void FromCensus_WithTheEmptyDefault_PlansNoFolds()
    {
        var report = Report(
            Row("jsaa", projectEntries: 2, queued: 2),
            Row("job-search-ai-assistant", projectEntries: 1, queued: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBeEmpty();
    }

    private static ProjectIdCensusReport Report(params ProjectIdCensusRow[] rows) =>
        new([.. rows], 0, 0, 0, 0, []);

    private static ProjectIdCensusRow Row(
        string projectId,
        long projectEntries = 0,
        long queued = 0,
        long watches = 0,
        bool registered = false,
        string? registeredName = null,
        long discards = 0,
        long qualityRows = 0,
        long metricsRows = 0,
        long noiseRows = 0,
        long customEntries = 0,
        long sharedEntries = 0,
        long nullContextEntries = 0,
        long workspaces = 0,
        long workspaceEntries = 0,
        params string[] settingsKeys) =>
        new(projectId, registered, registeredName, projectEntries, customEntries, sharedEntries, workspaceEntries, nullContextEntries, 0, 0, 0, 0, 0, 0,
            queued, discards, qualityRows, watches, 0, 0, 0, workspaces, metricsRows, noiseRows, settingsKeys);
}
