using System.Text;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

public sealed partial class DecisionServiceTests
{
    [RetryFact]
    public async Task Latest_task_is_registration_order_including_an_empty_task()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, _, _) = await AcceptedPairAsync();
        await service.RegisterTaskAsync("a", new DecisionWrite("empty", "session", "register-empty"), "Empty", cancellationToken: ct);
        var latest = (await service.ExportAsync("a", cancellationToken: ct)).Value;
        latest.SelectedTaskIds.ShouldBe(new[] { "empty" });
        latest.Decisions.ShouldBeEmpty();
        await service.AmendAsync("a", new DecisionWrite("task", "session", "amend-old"), first, 1, "clarify", new DecisionContent("Amended", "c", "s", "o", "r"), ct);
        (await service.ExportAsync("a", cancellationToken: ct)).Value.SelectedTaskIds.ShouldBe(new[] { "empty" });
        var both = (await service.ExportAsync("a", new DecisionTaskSelector(last: 2), ct)).Value;
        both.Tasks.Count.ShouldBe(2);
        both.Decisions.Count.ShouldBe(2);
        var all = (await service.ExportAsync("a", new DecisionTaskSelector(all: true), ct)).Value;
        all.Counts.Decisions.ShouldBe(2);
        (await service.ExportAsync("b", cancellationToken: ct)).Value.Counts.Tasks.ShouldBe(0);
    }

    [RetryFact]
    public async Task Task_export_keeps_touch_history_boundaries_and_referenced_task_provenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        var linked = await service.LinkAsync("a", new DecisionWrite("task", "session", "link"), first, second, DecisionEdgeKind.DependsOn, "requires", revision, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("review", "session", "register-review"), "Review", cancellationToken: ct);
        await service.UnlinkAsync("a", new DecisionWrite("review", "session", "remove"), linked.Value.EdgeId, "obsolete prerequisite", linked.ProjectRevision + 1, ct);
        var export = (await service.ExportAsync("a", cancellationToken: ct)).Value;
        export.SelectedTaskIds.ShouldBe(new[] { "review" });
        export.Decisions.Single().Record.DecisionId.ShouldBe(first);
        export.Decisions.Single().Touches.Single().Reason.ShouldBe("edge-removed");
        export.Decisions.Single().Versions.Single().Content.Title.ShouldBe("First");
        export.BoundaryNodes.Single().DecisionId.ShouldBe(second);
        export.Edges.Single().RemovalTaskId.ShouldBe("review");
        export.ReferencedTasks.Single().TaskId.ShouldBe("task");
        var preview = (await service.PreviewAsync("a", cancellationToken: ct)).Value;
        preview.Total.Edges.ShouldBe(0);
        preview.Text.ShouldNotContain("--depends_on-->");
    }

    [RetryFact]
    public async Task Preview_has_a_unicode_budget_and_reports_omitted_empty_tasks_and_graph_items()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        await service.LinkAsync("a", new DecisionWrite("task", "session", "link"), first, second, DecisionEdgeKind.DependsOn, "requires", revision, ct);
        for (var i = 0; i < 12; i++)
        {
            var id = "empty-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await service.RegisterTaskAsync("a", new DecisionWrite(id, "session", "register-" + id), string.Concat(Enumerable.Repeat("🐾", 90)), cancellationToken: ct);
        }

        var preview = (await service.PreviewAsync("a", new DecisionPreviewOptions(new DecisionTaskSelector(all: true), MaxDecisions: 1, MaxChars: 1000), ct)).Value;
        preview.Text.EnumerateRunes().Count().ShouldBeLessThanOrEqualTo(1000);
        preview.Total.Tasks.ShouldBe(13);
        preview.Omitted.Tasks.ShouldBeGreaterThan(0);
        preview.Omitted.Decisions.ShouldBe(preview.Total.Decisions - preview.Shown.Decisions);
        preview.Text.ShouldContain("omitted");
        preview.Text.ShouldNotContain("\uFFFD");
        var roomy = (await service.PreviewAsync("a", new DecisionPreviewOptions(new DecisionTaskSelector(taskIds: ["task"])), ct)).Value;
        roomy.Text.ShouldContain("--depends_on-->");
        roomy.Shown.Edges.ShouldBe(1);
    }

    [RetryFact]
    public async Task Query_is_project_wide_and_precedents_require_current_accepted_valid_decisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("first", "session", "register-first"), "First", cancellationToken: ct);
        var accepted = await service.RecordAsync("a", new DecisionWrite("first", "session", "accepted"), new DecisionContent("Bank", "architecture", "persistence", "bank", "encryption persistence"), DecisionStatus.Accepted, ct);
        await service.RecordAsync("a", new DecisionWrite("first", "session", "proposed"), new DecisionContent("Draft", "architecture", "persistence", "bank", "encryption persistence"), cancellationToken: ct);
        await service.RecordAsync("a", new DecisionWrite("first", "session", "expired"), new DecisionContent("Expired", "architecture", "persistence", "bank", "encryption persistence", validUntil: new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero)), DecisionStatus.Accepted, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("empty", "session", "register-empty"), "Empty", cancellationToken: ct);
        var queried = (await service.QueryAsync("a", new DecisionQuery(Query: "persistence", Limit: 1), ct)).Value;
        queried.TotalMatched.ShouldBe(3);
        queried.HasMore.ShouldBeTrue();
        var next = (await service.QueryAsync("a", new DecisionQuery(Query: "persistence", Limit: 1, AfterId: queried.NextAfterId), ct)).Value;
        next.Decisions.Single().Decision.DecisionId.ShouldNotBe(queried.Decisions.Single().Decision.DecisionId);
        var precedents = (await service.PrecedentsAsync("a", "persistence", cancellationToken: ct)).Value;
        precedents.Decisions.Single().Decision.DecisionId.ShouldBe(accepted.Value.DecisionId);
        precedents.Method.ShouldBe("fts");
        (await service.QueryAsync("b", new DecisionQuery(Query: "persistence"), ct)).Value.TotalMatched.ShouldBe(0);
    }

    [RetryFact]
    public async Task Graph_preserves_causal_direction_and_explicit_depth_and_node_bounds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        await service.LinkAsync("a", new DecisionWrite("task", "session", "link"), first, second, DecisionEdgeKind.DependsOn, "requires", revision, ct);
        var upstream = (await service.GraphAsync("a", new DecisionGraphRequest(first), ct)).Value;
        upstream.Nodes.Count.ShouldBe(2);
        upstream.Edges.Single().SourceId.ShouldBe(first);
        upstream.Edges.Single().TargetId.ShouldBe(second);
        (await service.GraphAsync("a", new DecisionGraphRequest(first, DecisionDirection.Downstream), ct)).Value.Nodes.Count.ShouldBe(1);
        (await service.GraphAsync("a", new DecisionGraphRequest(second, DecisionDirection.Downstream), ct)).Value.Nodes.Count.ShouldBe(2);
        var bounded = (await service.GraphAsync("a", new DecisionGraphRequest(first, MaxNodes: 1), ct)).Value;
        bounded.Nodes.Count.ShouldBe(1);
        bounded.Edges.ShouldBeEmpty();
        bounded.Truncated.ShouldBeTrue();
        bounded.Reasons.ShouldContain("nodes");
        (await service.GraphAsync("a", new DecisionGraphRequest(first, Depth: 0), ct)).Value.Nodes.Count.ShouldBe(1);
    }
    [RetryFact]
    public async Task Intermediate_task_touch_exports_full_history_raw_imports_and_referenced_tasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("original", "s", "register-original"), "Original", cancellationToken: ct);
        var decision = await service.RecordAsync("a", new DecisionWrite("original", "s", "record"), new DecisionContent("One", "c", "s", "o", "r"), cancellationToken: ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("middle", "s", "register-middle"), "Middle", cancellationToken: ct);
        await service.AmendAsync("a", new DecisionWrite("middle", "s", "amend-middle"), decision.Value.DecisionId, 1, "second", new DecisionContent("Two", "c", "s", "o", "r"), ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("latest", "s", "register-latest"), "Latest", cancellationToken: ct);
        await service.AmendAsync("a", new DecisionWrite("latest", "s", "amend-latest"), decision.Value.DecisionId, 2, "third", new DecisionContent("Three", "c", "s", "o", "r"), ct);
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var store = new SqliteDecisionStore(new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), new SharedProjectScope());
        await store.WriteAsync("a", async (writer, token) =>
        {
            await writer.AddImportAsync(new DecisionImport("fixture", "external", decision.Value.DecisionId, "original", "digest", "{\"extra\":true}", "{}"), token);
            return true;
        }, ct);
        var selector = new DecisionTaskSelector(taskIds: ["middle"]);
        var export = (await service.ExportAsync("a", selector, ct)).Value;
        export.Decisions.Single().Versions.Count.ShouldBe(3);
        export.Decisions.Single().Touches.Single().Version.ShouldBe(2);
        export.Decisions.Single().Record.CurrentVersion.ShouldBe(3);
        export.ReferencedTasks.Select(task => task.TaskId).Order(StringComparer.Ordinal).ShouldBe(new[] { "latest", "original" });
        export.ImportRecords.Single().RawPayload.ShouldBe("{\"extra\":true}");
        (await service.QueryAsync("a", new DecisionQuery(Selector: selector), ct)).Value.Decisions.Single().Decision.Title.ShouldBe("Three");
    }

    [RetryFact]
    public async Task Preview_abbreviates_by_unicode_scalars_and_sanitizes_terminal_controls()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "s", "register"), "Task", cancellationToken: ct);
        var longText = string.Concat(Enumerable.Repeat("🐾", 500));
        await service.RecordAsync("a", new DecisionWrite("task", "s", "record"), new DecisionContent("Title\u001b[31m", "c", "s", longText, longText), cancellationToken: ct);
        var preview = (await service.PreviewAsync("a", new DecisionPreviewOptions(MaxChars: 2000), ct)).Value;
        preview.AbbreviatedFields.ShouldBe(2);
        preview.Text.ShouldNotContain("\u001b");
        preview.Text.ShouldNotContain("\uFFFD");
        var outcome = preview.Text.Split('\n').Single(line => line.StartsWith("  Outcome:", StringComparison.Ordinal))[11..];
        outcome.EnumerateRunes().Count().ShouldBe(240);
    }

    [RetryFact]
    public async Task Context_cycles_are_opt_in_and_contradictions_have_symmetric_replay_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        var one = await service.LinkAsync("a", new DecisionWrite("task", "s", "support-one"), first, second, DecisionEdgeKind.Supports, "supports", revision, ct);
        var two = await service.LinkAsync("a", new DecisionWrite("task", "s", "support-two"), second, first, DecisionEdgeKind.Supports, "supports", one.ProjectRevision, ct);
        var write = new DecisionWrite("task", "s", "contradicts");
        var contradiction = await service.LinkAsync("a", write, second, first, DecisionEdgeKind.Contradicts, "conflicts", two.ProjectRevision, ct);
        var replay = await service.LinkAsync("a", write, first, second, DecisionEdgeKind.Contradicts, "conflicts", two.ProjectRevision, ct);
        replay.Replayed.ShouldBeTrue();
        replay.Value.EdgeId.ShouldBe(contradiction.Value.EdgeId);
        (await service.GraphAsync("a", new DecisionGraphRequest(first), ct)).Value.Nodes.Count.ShouldBe(1);
        var graph = (await service.GraphAsync("a", new DecisionGraphRequest(first, IncludeContext: true), ct)).Value;
        graph.Nodes.Count.ShouldBe(2);
        graph.Edges.Count.ShouldBe(3);
    }
}
