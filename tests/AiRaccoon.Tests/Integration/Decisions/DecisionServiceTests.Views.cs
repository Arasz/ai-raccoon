using System.Text;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Decisions;
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
    public async Task Search_accepts_long_queries_with_more_than_64_terms()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var query = string.Join(" ", Enumerable.Repeat("x", 1000));
        (await service.QueryAsync("a", new DecisionQuery(Query: query), ct)).Value.Method.ShouldBe("fts");
        (await service.PrecedentsAsync("a", query, cancellationToken: ct)).Value.Method.ShouldBe("fts");
    }

    [RetryFact]
    public async Task Preview_omits_whole_decisions_when_text_budget_is_exhausted()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "session", "register"), "Task", cancellationToken: ct);
        var content = new DecisionContent("Decision", "category", "scenario", new string('o', 180), new string('r', 180));
        await service.RecordAsync("a", new DecisionWrite("task", "session", "one"), content, cancellationToken: ct);
        await service.RecordAsync("a", new DecisionWrite("task", "session", "two"), content, cancellationToken: ct);
        var result = (await service.PreviewAsync("a", new DecisionPreviewOptions(MaxChars: 1000), ct)).Value;
        result.Text.EnumerateRunes().Count().ShouldBeLessThanOrEqualTo(1000);
        result.Shown.Decisions.ShouldBe(1);
        result.Omitted.Decisions.ShouldBe(1);
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
        upstream.Truncated.ShouldBeFalse();
        upstream.Reasons.ShouldBeEmpty();
        upstream.OmittedNodes.ShouldBe(0);
        upstream.OmittedEdges.ShouldBe(0);
        var shallow = (await service.GraphAsync("a", new DecisionGraphRequest(first, Depth: 0), ct)).Value;
        shallow.Nodes.Single().DecisionId.ShouldBe(first);
        shallow.Edges.ShouldBeEmpty();
        shallow.Truncated.ShouldBeTrue();
        shallow.Reasons.ShouldBe(["depth"]);
        shallow.OmittedNodes.ShouldBeNull();
        shallow.OmittedEdges.ShouldBeNull();
    }

    /// <summary>A chain A→B→C→D: each extra depth step reaches exactly one more decision, and a cut chain says why.</summary>
    [RetryFact]
    public async Task Graph_follows_a_chain_one_step_per_depth()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "s", "register"), "Task", cancellationToken: ct);
        var chain = new List<string>();
        foreach (var name in new[] { "A", "B", "C", "D" })
        {
            chain.Add((await service.RecordAsync("a", new DecisionWrite("task", "s", "record-" + name),
                new DecisionContent(name, "architecture", "s", "o", "r"), DecisionStatus.Accepted, ct)).Value.DecisionId);
        }

        var revision = (await service.ExportAsync("a", cancellationToken: ct)).ProjectRevision;
        for (var i = 0; i < 3; i++)
        {
            revision = (await service.LinkAsync("a", new DecisionWrite("task", "s", $"link-{i}"), chain[i], chain[i + 1],
                DecisionEdgeKind.DependsOn, "requires", revision, ct)).ProjectRevision;
        }

        var two = (await service.GraphAsync("a", new DecisionGraphRequest(chain[0], Depth: 2), ct)).Value;
        two.Nodes.Select(node => node.DecisionId).ShouldBe(chain.Take(3), ignoreOrder: true);
        two.Edges.Count.ShouldBe(2);
        two.Truncated.ShouldBeTrue();
        two.Reasons.ShouldBe(["depth"]);

        var three = (await service.GraphAsync("a", new DecisionGraphRequest(chain[0], Depth: 3), ct)).Value;
        three.Nodes.Select(node => node.DecisionId).ShouldBe(chain, ignoreOrder: true);
        three.Edges.Count.ShouldBe(3);
        three.Truncated.ShouldBeFalse();
    }

    /// <summary>Edges among the visited nodes past the internal cap are cut and counted, not silently dropped.</summary>
    [RetryFact]
    public async Task Graph_cuts_edges_past_the_internal_cap_and_counts_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "s", "register"), "Task", cancellationToken: ct);
        var ids = new List<string>();
        for (var i = 0; ids.Count * (ids.Count - 1) <= DecisionService.GraphEdgeLimit; i++)
        {
            var recorded = await service.RecordAsync("a", new DecisionWrite("task", "s", $"record-{i}"),
                new DecisionContent($"D{i}", "architecture", "s", "o", "r"), DecisionStatus.Accepted, ct);
            ids.Add(recorded.Value.DecisionId);
        }

        var pairs = ids.SelectMany(source => ids.Where(target => target != source).Select(target => (source, target)))
            .Take(DecisionService.GraphEdgeLimit + 1).ToArray();
        await Store().WriteAsync("a", async (writer, token) =>
        {
            var revision = await writer.RevisionAsync(token) + 1;
            foreach (var (source, target) in pairs)
            {
                await writer.AddEdgeAsync(new DecisionEdge(Guid.CreateVersion7().ToString("N"), source, target, DecisionEdgeKind.Supports,
                    "seeded", "task", revision, DateTimeOffset.UnixEpoch, "s", "s"), token);
            }

            await writer.SetRevisionAsync(revision, token);
            return 0;
        }, ct);

        var graph = (await service.GraphAsync("a", new DecisionGraphRequest(ids[0], DecisionDirection.Both, Depth: 1, MaxNodes: 500, IncludeContext: true), ct)).Value;

        graph.Nodes.Count.ShouldBe(ids.Count);
        graph.Edges.Count.ShouldBe(2000);
        graph.Truncated.ShouldBeTrue();
        graph.Reasons.ShouldBe(["edges"]);
        graph.OmittedNodes.ShouldBe(0);
        graph.OmittedEdges.ShouldBe(1);
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
