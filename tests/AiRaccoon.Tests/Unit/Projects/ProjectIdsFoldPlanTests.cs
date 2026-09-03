using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     Air-merge P2: the repair folds loser ids into their canonical winners (and deletes drop
///     candidates) from a durable plan derived from the P1 census plus the P1 alias map — never
///     from hand-written id lists at the repair site, so pull-time fold and the ToolGate fold keep
///     consuming the same table.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsFoldPlanTests
{
    [Fact]
    public void ProjectIds_RepairKind_MapsToTheProjectIdsOutboxKey()
    {
        RepairKind.ProjectIds.ToKey().ShouldBe("project-ids");
        RepairKinds.ProjectIds.ShouldBe("project-ids");
    }

    [Fact]
    public void FromCensus_MapsKnownLosersToTheirWinners()
    {
        var report = Report(
            Row("jsaa", projectEntries: 2, queued: 2),
            Row("job-search-ai-assistant", projectEntries: 1, queued: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBe([new ProjectIdFold("job-search-ai-assistant", "jsaa")]);
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBeEmpty();
        plan.Unresolved.ShouldBeEmpty();
    }

    [Fact]
    public void FromCensus_FoldsAGuidLoser_ByItsProjectsRowName()
    {
        // The live 01a062f4 guid is registered under its pre-guid name; the alias map never
        // hardcodes the guid itself (P1 decision), so the plan resolves it through that name.
        var guid = "01a062f4-0000-7000-8000-000000000001";
        var report = Report(Row(guid, projectEntries: 2, registered: true, registeredName: "job-search-ai-assistant"));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBe([new ProjectIdFold(guid, "jsaa")]);
    }

    [Fact]
    public void FromCensus_FoldsCaseSplitLosers_IntoTheLowercaseWinner()
    {
        var report = Report(
            Row("ai-raccoon", projectEntries: 1),
            Row("AI-RACCOON", projectEntries: 1, settingsKeys: ["ingest.scope.AI-RACCOON"]));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBe([new ProjectIdFold("AI-RACCOON", "ai-raccoon")]);
    }

    [Fact]
    public void FromCensus_DeletesDroppedIds_InsteadOfFoldingThem()
    {
        var report = Report(
            Row("qa-noise-project", projectEntries: 1),
            Row("manual-sweep", projectEntries: 1, watches: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBe(["qa-noise-project", "manual-sweep"], "the plan preserves census order");
    }

    [Fact]
    public void FromCensus_RetiresZeroEntryProjectsRows_WithNoAttachments()
    {
        var guid = "01a03024-0000-7000-8000-000000000001";
        var report = Report(Row(guid, registered: true, registeredName: "ai-badger"));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBe([guid]);
        plan.Unresolved.ShouldBeEmpty();
    }

    [Fact]
    public void FromCensus_LeavesTrueTyposUnresolved_NeverFolded()
    {
        // A typo is nobody's alias and nobody's name: P3 refuses its future writes, but P2 must
        // not move or delete rows it cannot attribute.
        var report = Report(Row("jsaaa", projectEntries: 1));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.Folds.ShouldBeEmpty();
        plan.Dropped.ShouldBeEmpty();
        plan.RetiredProjects.ShouldBeEmpty();
        plan.Unresolved.ShouldBe(["jsaaa"]);
    }

    [Fact]
    public void FromCensus_CanonicalSelf_WithEntries_IsANoOp()
    {
        var report = Report(Row("jsaa", projectEntries: 2, queued: 2));

        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        plan.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void FromCensus_OnACleanBank_IsEmpty()
    {
        var plan = ProjectIdsFoldPlan.FromCensus(
            new ProjectIdCensusReport([], 0, 0, 0, 0, []), ProjectIdAliasMap.Default);

        plan.IsEmpty.ShouldBeTrue();
        plan.Unresolved.ShouldBeEmpty();
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
        params string[] settingsKeys) =>
        new(projectId, registered, registeredName, projectEntries, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            queued, 0, 0, watches, 0, 0, 0, 0, 0, 0, settingsKeys);
}
