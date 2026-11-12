using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Infrastructure.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SemanticaImportPlanTests
{
    private const string Db05 = "db05c33e-6e9e-458c-b6be-d9f3be2c51eb";
    private const string D120 = "120d7c0a-22c3-4b20-b6d5-b1994452d380";
    private const string D33 = "33da32fb-4d6e-49e0-9dc2-00172691389a";
    private const string C1d5 = "c1d5977e-64f5-4ef8-b067-93bc7fe896f6";

    private static readonly string[] BothTasks = ["semantica-legacy", "storage-task"];

    private static SemanticaImportPlan Plan(byte[] source, byte[] mapping, string[]? tasks = null,
        IReadOnlyList<DecisionImport>? imports = null, IReadOnlyList<DecisionEdge>? edges = null, IReadOnlyDictionary<string, string>? ids = null)
    {
        var export = SemanticaExport.Parse(source);
        return SemanticaImportPlan.Build(export, SemanticaMapping.Parse(mapping),
            [.. (tasks ?? BothTasks).Select((id, index) => new DecisionTask(id, id, index + 1, DateTimeOffset.UnixEpoch, null))],
            imports ?? [], edges ?? [], ids ?? Mint(export));
    }

    private static Dictionary<string, string> Mint(SemanticaExport export) =>
        export.Entities.Where(entity => entity.Type == "decision").Select(entity => entity.Id).Distinct().ToDictionary(id => id, id => $"n-{id}");

    private static SemanticaImportPlan FixturePlan(IReadOnlyList<DecisionImport>? imports = null, IReadOnlyDictionary<string, string>? ids = null) =>
        Plan(SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json"), SemanticaParsingTests.Fixture("export-graph-json-0.7.0.mapping.json"), imports: imports, ids: ids);

    private static string[] Codes(IEnumerable<DecisionImportIssue> issues) => [.. issues.Select(issue => issue.Code)];

    private static string DecisionJson(string id, string? text = "Title", string properties = """{"category":"c","scenario":"s","outcome":"o","reasoning":"r"}""") =>
        $"{{\"id\":{Json(id)}{(text is null ? "" : $",\"text\":{Json(text)}")},\"type\":\"decision\"{(properties is null ? "" : $",\"properties\":{properties}")}}}";

    private static string Json(string value) => JsonSerializer.Serialize(value);

    private static string Link(string id, string from, string to, string type) => $"{{\"id\":{Json(id)},\"source_id\":{Json(from)},\"target_id\":{Json(to)},\"type\":{Json(type)}}}";

    private static byte[] Export(string[] entities, string[]? relationships = null)
    {
        relationships ??= [];
        var inner =
            $"{{\"entities\":[{string.Join(",", entities)}],\"relationships\":[{string.Join(",", relationships)}],\"statistics\":{{\"entity_count\":{entities.Length},\"relationship_count\":{relationships.Length}}}}}";
        return SemanticaParsingTests.Envelope(inner);
    }

    /// <summary>Builds a mapping from (originalId, status) pairs; every id gets externalId = id.</summary>
    private static byte[] Mapping(IEnumerable<(string Id, string? Status)> records, string edgeKinds = """{"depends_on":"depends_on","supports":"supports","contradicts":"contradicts"}""",
        string ns = "ns", string legacy = "semantica-legacy", Action<JsonObject>? edit = null)
    {
        var map = new JsonObject();
        foreach (var (id, status) in records)
        {
            var record = new JsonObject { ["externalId"] = id };
            if (status is not null)
            {
                record["status"] = status;
            }

            map[id] = record;
        }

        edit?.Invoke(map);
        return Encoding.UTF8.GetBytes(new JsonObject
        {
            ["sourceNamespace"] = ns,
            ["legacyTaskId"] = legacy,
            ["edgeKinds"] = JsonNode.Parse(edgeKinds),
            ["records"] = map,
        }.ToJsonString());
    }

    [Fact]
    public void Plan_Fixture_MatchesExpectedCounts()
    {
        var plan = FixturePlan();

        plan.Blockers.ShouldBeEmpty();
        plan.Decisions.Count.ShouldBe(4);
        plan.Edges.Count.ShouldBe(3);
        plan.Counts.ShouldBe(new DecisionImportCounts(9, 13, 4, 3, 15, 0, 0));
        Codes(plan.Notices).Count(code => code == "unsupported-entity").ShouldBe(5);
        Codes(plan.Notices).Count(code => code == "unsupported-relationship").ShouldBe(10);
        plan.Notices.Count.ShouldBe(15);
        var rows = plan.Decisions.Select(decision => decision.Import).Concat(plan.RetainedImports).ToArray();
        rows.Length.ShouldBe(22);
        rows.Count(row => row.DecisionId is not null).ShouldBe(4);
        rows.ShouldAllBe(row => row.SourceNamespace == "semantica-main");
        plan.Decisions.Select(decision => (decision.Import.ExternalId, decision.Status)).ShouldBe(
        [
            (Db05, DecisionStatus.Accepted), (D120, DecisionStatus.Accepted), (D33, DecisionStatus.Rejected), (C1d5, DecisionStatus.Proposed),
        ]);
        plan.Decisions.Single(decision => decision.Import.ExternalId == D33).TaskId.ShouldBe("storage-task");
        plan.Decisions[0].TaskId.ShouldBe("semantica-legacy");
        plan.Decisions[0].Content.Title.ShouldBe("Choose where decisions live");
        plan.Decisions[0].Content.Confidence.ShouldBe(0.9);
        plan.Decisions[0].Content.Maker.ShouldBe("mcp_client");
        plan.Decisions[2].Content.Outcome.ShouldBe("rejected");

        plan.Edges.Select(edge => (edge.SourceId, edge.TargetId, edge.Kind)).ShouldBe(
        [
            ($"n-{D120}", $"n-{Db05}", DecisionEdgeKind.DependsOn),
            ($"n-{C1d5}", $"n-{D120}", DecisionEdgeKind.Supports),
            ($"n-{D33}", $"n-{Db05}", DecisionEdgeKind.Contradicts).Canonical(),
        ]);
        plan.Edges[0].Reason.ShouldBe("Imported from Semantica semantica-main/c5755547-d909-50d3-86dd-600728885407 (depends_on).");
        var person = plan.RetainedImports.Single(row => row.ExternalId == "person-1");
        person.RawPayload.ShouldBe(SemanticaExport.Parse(SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json")).Entities.Single(entity => entity.Id == "person-1").RawText);
        person.RawPayload.ShouldContain("\"id\": \"person-1\"");
        person.SourceDigest.ShouldBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(person.RawPayload))));
        SemanticaImportPlan.Digest(SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json")).ShouldBe("290A36C00C2DDE2CBA4C32243E71F5244D9FEA0D8F8F17B5560AD475B74F5A11");
        JsonNode.Parse(plan.Decisions[0].Import.Mapping)!.AsObject().Select(pair => pair.Key).ShouldBe(["originalId", "recordKind", "type", "externalId", "taskId", "status"]);
    }

    [Fact]
    public void Plan_NeverInfersStatusFromOutcome()
    {
        var source = Export([DecisionJson("a", properties: """{"category":"c","scenario":"s","outcome":"rejected","reasoning":"r"}""")]);

        var plan = Plan(source, Mapping([("a", null)]));

        Codes(plan.Blockers).ShouldBe(["missing-status"]);
        plan.Blockers[0].Path.ShouldBe("entities[0]");
        plan.Decisions.ShouldBeEmpty();
        plan.CanApply.ShouldBeFalse();
    }

    [Theory]
    [InlineData("nonsense", "unknown-status")]
    [InlineData("superseded", "superseded-status")]
    [InlineData("1", "unknown-status")]
    public void Plan_UnusableStatus_Blocks(string status, string code) =>
        Codes(Plan(Export([DecisionJson("a")]), Mapping([("a", status)])).Blockers).ShouldBe([code]);

    [Theory]
    [InlineData("Deprecated")]
    [InlineData("ACCEPTED")]
    [InlineData("Superseded")]
    public void Plan_StatusMatchesExactlyAndCaseSensitively(string status) =>
        Codes(Plan(Export([DecisionJson("a")]), Mapping([("a", status)])).Blockers).ShouldBe(["unknown-status"]);

    [Fact]
    public void Plan_MappingEntryForARecordNotInTheExport_Blocks()
    {
        var plan = Plan(Export([DecisionJson("a")]), Mapping([("a", "accepted"), ("ghost", null)]));

        plan.Blockers.Single().ShouldBe(new DecisionImportIssue("unknown-record", "mapping.records.ghost", "The mapping lists a record the export does not contain.", "ghost"));
    }

    [Fact]
    public void Plan_StatusOrTitleOnNonDecision_Blocks()
    {
        var source = Export(["""{"id":"cat","type":"category"}"""], [Link("r", "cat", "cat", "x")]);

        var plan = Plan(source, Mapping([("cat", "accepted"), ("r", null)], edit: map => map["r"]!["title"] = "T"));

        Codes(plan.Blockers).ShouldBe(["status-on-non-decision", "invalid-content"]);
    }

    public static TheoryData<string, string> BadContent => new()
    {
        { """{"category":"c","scenario":"s","outcome":"o"}""", "entities[0].properties.reasoning" },
        { """{"category":"c","scenario":"s","reasoning":"r"}""", "entities[0].properties.outcome" },
        { """{"scenario":"s","outcome":"o","reasoning":"r"}""", "entities[0].properties.category" },
        { """{"category":5,"scenario":"s","outcome":"o","reasoning":"r"}""", "entities[0].properties.category" },
        { """{"category":"c","scenario":"\ud800","outcome":"o","reasoning":"r"}""", "entities[0].properties.scenario" },
        { """{"category":"c","scenario":"s","outcome":"o","reasoning":"r","confidence":"high"}""", "entities[0].properties.confidence" },
        { """{"category":"c","scenario":"s","outcome":"o","reasoning":"r","confidence":1.5}""", "entities[0]" },
        { """{"category":"c","scenario":"s","outcome":"o","reasoning":"r","decision_maker":7}""", "entities[0].properties.decision_maker" },
        { """{"category":"c","scenario":"s","outcome":"o","reasoning":"r","decision_maker":"a b\u0001"}""", "entities[0]" },
        { "5", "entities[0].properties" },
        { "[]", "entities[0].properties" },
        { null!, "entities[0].properties" },
    };

    [Theory]
    [MemberData(nameof(BadContent))]
    public void Plan_OverlongOrMissingContent_BlocksPerRecord(string? properties, string path)
    {
        var plan = Plan(Export([DecisionJson("a", properties: properties!)]), Mapping([("a", "accepted")]));

        Codes(plan.Blockers).ShouldBe(["invalid-content"]);
        plan.Blockers[0].Path.ShouldBe(path);
        plan.Blockers[0].RecordId.ShouldBe("a");
        plan.Decisions.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_OverlongTitle_BlocksWithoutTruncating_AndTitleOverrideRescues()
    {
        var source = Export([DecisionJson("a", text: new string('x', 201))]);

        Codes(Plan(source, Mapping([("a", "accepted")])).Blockers).ShouldBe(["invalid-content"]);
        var rescued = Plan(source, Mapping([("a", "accepted")], edit: map => map["a"]!["title"] = "Short"));
        rescued.Blockers.ShouldBeEmpty();
        rescued.Decisions.Single().Content.Title.ShouldBe("Short");
        JsonNode.Parse(rescued.Decisions.Single().Import.Mapping)!["title"]!.GetValue<string>().ShouldBe("Short");
    }

    [Fact]
    public void Plan_MissingTextWithoutTitleOverride_Blocks() =>
        Codes(Plan(Export([DecisionJson("a", text: null)]), Mapping([("a", "accepted")])).Blockers).ShouldBe(["invalid-content"]);

    [Fact]
    public void Plan_MappedEdgeToNonDecision_Blocks()
    {
        var source = Export([DecisionJson("a"), """{"id":"cat","type":"category"}"""], [Link("r1", "a", "cat", "depends_on"), Link("r2", "a", "missing", "supports")]);

        var plan = Plan(source, Mapping([("a", "accepted"), ("cat", null), ("r1", null), ("r2", null)]));

        Codes(plan.Blockers).ShouldBe(["unresolved-endpoint", "unresolved-endpoint"]);
        plan.Blockers.Select(blocker => blocker.Path).ShouldBe(["relationships[0]", "relationships[1]"]);
        plan.Edges.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_UnmappedCaused_RetainedNotPromoted()
    {
        var plan = FixturePlan();

        plan.Edges.ShouldNotContain(edge => edge.Reason.Contains("CAUSED"));
        plan.RetainedImports.Single(row => row.ExternalId == "bb8831a1-e0fa-55c8-a585-f17a28eb988d").DecisionId.ShouldBeNull();
        plan.Notices.ShouldContain(notice => notice.Code == "unsupported-relationship" && notice.RecordId == "bb8831a1-e0fa-55c8-a585-f17a28eb988d");
        var source = Export([DecisionJson("a"), DecisionJson("b")], [Link("r", "a", "b", "CAUSED")]);
        var none = Plan(source, Mapping([("a", "accepted"), ("b", "accepted"), ("r", null)], edgeKinds: "{}"));
        none.Edges.ShouldBeEmpty();
        none.Blockers.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_UnlistedRelationshipWithEndpointOutsideTheFile_NoticesAndRetains()
    {
        var plan = Plan(Export([DecisionJson("a")], [Link("r", "a", "ghost", "CAUSED")]), Mapping([("a", "accepted"), ("r", null)]));

        plan.Blockers.ShouldBeEmpty();
        Codes(plan.Notices).ShouldBe(["unsupported-relationship", "unresolved-endpoint"]);
        plan.RetainedImports.Single().ExternalId.ShouldBe("r");
    }

    [Fact]
    public void Plan_EdgeKindOutsideTheAllowedSet_Blocks()
    {
        var source = Export([DecisionJson("a"), DecisionJson("b")], [Link("r", "a", "b", "replaces")]);

        var plan = Plan(source, Mapping([("a", "accepted"), ("b", "accepted"), ("r", null)], edgeKinds: """{"replaces":"supersedes","other":"nonsense"}"""));

        Codes(plan.Blockers).ShouldBe(["invalid-edge-kind", "invalid-edge-kind"]);
        plan.Edges.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_EndpointAlreadyImported_ResolvesThroughItsImportRow()
    {
        var first = Plan(Export([DecisionJson("a"), DecisionJson("b")]), Mapping([("a", "accepted"), ("b", "accepted")]));
        var rows = first.Decisions.Select(decision => decision.Import).ToArray();
        var newer = Export([DecisionJson("a"), DecisionJson("b"), DecisionJson("c")], [Link("r", "c", "a", "depends_on")]);

        var plan = Plan(newer, Mapping([("a", "accepted"), ("b", "accepted"), ("c", "proposed"), ("r", null)]), imports: rows,
            ids: new Dictionary<string, string> { ["a"] = "n-new-a", ["b"] = "n-new-b", ["c"] = "n-c" });

        plan.Blockers.ShouldBeEmpty();
        plan.Decisions.Select(decision => decision.Import.ExternalId).ShouldBe(["c"]);
        plan.Edges.Single().ShouldSatisfyAllConditions(
            edge => edge.SourceId.ShouldBe("n-c"),
            edge => edge.TargetId.ShouldBe("n-a"));
        plan.Counts.AlreadyImported.ShouldBe(2);
        Codes(plan.Notices).Count(code => code == "already-imported").ShouldBe(2);
    }

    [Fact]
    public void Plan_EndpointOnlyInAnEarlierImport_ResolvesWithoutAMappingEntry()
    {
        var first = Plan(Export([DecisionJson("a")]), Mapping([("a", "accepted")]));
        var plan = Plan(Export([DecisionJson("c")], [Link("r", "c", "a", "supports")]), Mapping([("c", "proposed"), ("r", null)]),
            imports: [first.Decisions.Single().Import]);

        plan.Blockers.ShouldBeEmpty();
        plan.Edges.Single().TargetId.ShouldBe("n-a");
    }

    [Fact]
    public void Plan_DuplicateRules_CollapseEqualRefuseDifferent()
    {
        var equal = Plan(Export([DecisionJson("a"), DecisionJson("a")]), Mapping([("a", "accepted")]));
        equal.Blockers.ShouldBeEmpty();
        Codes(equal.Notices).ShouldBe(["collapsed-duplicate"]);
        equal.Decisions.Count.ShouldBe(1);
        equal.Counts.Collapsed.ShouldBe(1);

        var different = Plan(Export([DecisionJson("a"), DecisionJson("a", text: "Other")]), Mapping([("a", "accepted")]));
        Codes(different.Blockers).ShouldBe(["duplicate-record"]);

        var crossKind = Plan(Export([DecisionJson("a")], [Link("a", "a", "a", "x")]), Mapping([("a", "accepted")]));
        Codes(crossKind.Blockers).ShouldBe(["duplicate-record"]);

        var sameExternal = Plan(Export([DecisionJson("a"), DecisionJson("b")]), Mapping([("a", "accepted"), ("b", "accepted")],
            edit: map => map["b"]!["externalId"] = "a"));
        Codes(sameExternal.Blockers).ShouldBe(["duplicate-external-id"]);
    }

    [Fact]
    public void Plan_DependsOnCycleAndContradictsOrder()
    {
        var entities = new[] { DecisionJson("a"), DecisionJson("b"), DecisionJson("c") };
        var ids = new Dictionary<string, string> { ["a"] = "n-1", ["b"] = "n-2", ["c"] = "n-3" };
        static (string, string?)[] Records(params string[] links) =>
            [("a", "accepted"), ("b", "accepted"), ("c", "accepted"), .. links.Select(link => (link, (string?)null))];

        var cycle = Plan(Export(entities, [Link("r1", "a", "b", "depends_on"), Link("r2", "b", "c", "depends_on"), Link("r3", "c", "a", "depends_on")]), Mapping(Records("r1", "r2", "r3")), ids: ids);
        Codes(cycle.Blockers).ShouldBe(["invalid-link"]);
        cycle.Blockers[0].Path.ShouldBe("relationships[2]");
        cycle.Edges.Count.ShouldBe(2);

        var self = Plan(Export(entities, [Link("r1", "a", "a", "supports")]), Mapping(Records("r1")), ids: ids);
        Codes(self.Blockers).ShouldBe(["invalid-link"]);

        var liveCycle = new DecisionEdge("e", "n-2", "n-1", DecisionEdgeKind.DependsOn, "r", "semantica-legacy", 1, DateTimeOffset.UnixEpoch, "x", "s");
        Codes(Plan(Export(entities, [Link("r1", "a", "b", "depends_on")]), Mapping(Records("r1")), edges: [liveCycle], ids: ids).Blockers).ShouldBe(["invalid-link"]);

        var order = Plan(Export(entities, [Link("r1", "b", "a", "contradicts")]), Mapping(Records("r1")), ids: new Dictionary<string, string> { ["a"] = "n-9", ["b"] = "n-1", ["c"] = "n-3" });
        order.Edges.Single().ShouldSatisfyAllConditions(edge => edge.SourceId.ShouldBe("n-1"), edge => edge.TargetId.ShouldBe("n-9"));
        var fixtureOrder = FixturePlan(ids: Mint(SemanticaExport.Parse(SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json")))
            .ToDictionary(pair => pair.Key, pair => pair.Key == Db05 ? $"z-{pair.Key}" : $"a-{pair.Key}"));
        fixtureOrder.Edges[2].SourceId.ShouldBe($"a-{D33}");
        fixtureOrder.Edges[2].TargetId.ShouldBe($"z-{Db05}");
    }

    [Fact]
    public void Plan_RepeatedEdge_CollapsesAgainstLiveAndPlanned()
    {
        var entities = new[] { DecisionJson("a"), DecisionJson("b") };
        var records = new[] { ("a", (string?)"accepted"), ("b", "accepted"), ("r1", null), ("r2", null) };
        var planned = Plan(Export(entities, [Link("r1", "a", "b", "supports"), Link("r2", "a", "b", "supports")]), Mapping(records));
        planned.Blockers.ShouldBeEmpty();
        planned.Edges.Count.ShouldBe(1);
        planned.Counts.ShouldBe(new DecisionImportCounts(2, 2, 2, 1, 1, 0, 1));
        planned.Notices.Count(notice => notice.Code == "collapsed-edge").ShouldBe(1);
        planned.RetainedImports.Count.ShouldBe(2);

        var live = new DecisionEdge("e", "n-a", "n-b", DecisionEdgeKind.Supports, "r", "semantica-legacy", 1, DateTimeOffset.UnixEpoch, "x", "s");
        var against = Plan(Export(entities, [Link("r1", "a", "b", "supports")]), Mapping(records), edges: [live]);
        against.Edges.ShouldBeEmpty();
        against.Counts.Collapsed.ShouldBe(1);
        against.RetainedImports.Select(row => row.ExternalId).ShouldBe(["r1"]);

        var removed = live with { RemovedRevision = 2 };
        Plan(Export(entities, [Link("r1", "a", "b", "supports")]), Mapping(records), edges: [removed]).Edges.Count.ShouldBe(1);
    }

    [Fact]
    public void Plan_MappingProblems_AreBlockers()
    {
        var source = Export([DecisionJson("a")]);

        Codes(Plan(source, Mapping([])).Blockers).ShouldBe(["unmapped-record"]);
        Codes(Plan(source, Mapping([("a", "accepted"), ("ghost", null)])).Blockers).ShouldBe(["unknown-record"]);
        Codes(Plan(source, Mapping([("a", "accepted")], edit: map => map["a"]!["externalId"] = " padded")).Blockers).ShouldBe(["invalid-identifier"]);
        Codes(Plan(source, Mapping([("a", "accepted")], edit: map => map["a"]!.AsObject().Remove("externalId"))).Blockers).ShouldBe(["invalid-identifier"]);
        Codes(Plan(source, Mapping([("a", "accepted")], edit: map => map["a"]!["taskId"] = "nowhere")).Blockers).ShouldBe(["unknown-task"]);
        Codes(Plan(source, Mapping([("a", "accepted")], edit: map => map["a"]!["taskId"] = "bad id\u0001")).Blockers).ShouldBe(["invalid-identifier"]);
        Codes(Plan(source, Mapping([("a", "accepted")], ns: " bad")).Blockers).ShouldBe(["invalid-identifier"]);
        Codes(Plan(source, Mapping([("a", "accepted")], legacy: " bad")).Blockers).ShouldBe(["invalid-identifier"]);
    }

    [Fact]
    public void Plan_EmptyExportOnAFreshProject_BlocksOnTheLegacyTask()
    {
        var plan = Plan(Export([]), Mapping([]), tasks: []);

        Codes(plan.Blockers).ShouldBe(["unknown-task"]);
        plan.Blockers[0].Path.ShouldBe("mapping.legacyTaskId");
    }

    [Fact]
    public void Plan_ChangedSourceOrMapping_Blocks_AndIdenticalIsAlreadyImported()
    {
        var mapping = Mapping([("a", "accepted")]);
        var rows = Plan(Export([DecisionJson("a")]), mapping).Decisions.Select(decision => decision.Import).ToArray();

        var same = Plan(Export([DecisionJson("a")]), mapping, imports: rows);
        same.Blockers.ShouldBeEmpty();
        same.Counts.AlreadyImported.ShouldBe(1);
        same.Decisions.ShouldBeEmpty();
        same.HasNewRows.ShouldBeFalse();

        Codes(Plan(Export([DecisionJson("a", text: "Edited")]), mapping, imports: rows).Blockers).ShouldBe(["changed-source"]);
        Codes(Plan(Export([DecisionJson("a")]), Mapping([("a", "deprecated")]), imports: rows).Blockers).ShouldBe(["changed-mapping"]);
        Codes(Plan(Export([DecisionJson("a")]), Mapping([("a", "accepted")], edit: map => map["a"]!["taskId"] = "storage-task"), imports: rows).Blockers).ShouldBe(["changed-mapping"]);
        Plan(Export([DecisionJson("a")]), Mapping([("a", "accepted")], ns: "other"), imports: rows).Decisions.Count.ShouldBe(1);
    }

    [Fact]
    public void Plan_RepeatingTheFixtureAfterImport_WritesNothing()
    {
        var first = FixturePlan();
        var rows = first.Decisions.Select(decision => decision.Import).Concat(first.RetainedImports).ToArray();

        var again = FixturePlan(rows);

        again.Blockers.ShouldBeEmpty();
        again.HasNewRows.ShouldBeFalse();
        again.Counts.ShouldBe(new DecisionImportCounts(9, 13, 0, 0, 0, 22, 0));
    }

    [Fact]
    public void ToReport_CapsEachListAndCountsTheRest()
    {
        var entities = Enumerable.Range(0, 250).Select(index => DecisionJson($"d{index}")).ToArray();

        var plan = Plan(Export(entities), Mapping(entities.Select((_, index) => ($"d{index}", (string?)null))));
        var report = plan.ToReport("S", "M");

        plan.Blockers.Count.ShouldBe(250);
        report.Blockers.Count.ShouldBe(DecisionImportLimits.MaxIssues);
        report.OmittedBlockers.ShouldBe(50);
        report.CanApply.ShouldBeFalse();
        report.SourceDigest.ShouldBe("S");
        report.MappingDigest.ShouldBe("M");
        report.OmittedNotices.ShouldBe(0);
    }
}

internal static class EdgeTupleExtensions
{
    /// <summary>Orders a contradicts pair the way the planner does: ordinal, smaller id first.</summary>
    public static (string SourceId, string TargetId, DecisionEdgeKind Kind) Canonical(this (string SourceId, string TargetId, DecisionEdgeKind Kind) edge) =>
        StringComparer.Ordinal.Compare(edge.SourceId, edge.TargetId) > 0 ? (edge.TargetId, edge.SourceId, edge.Kind) : edge;
}
