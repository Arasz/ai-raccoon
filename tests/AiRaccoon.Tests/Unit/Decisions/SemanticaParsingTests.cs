using System.Text;
using System.Text.Json;
using AiRaccoon.Infrastructure.Decisions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Decisions;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SemanticaParsingTests
{
    private const string EmptyStatistics = """{"entity_count":0,"relationship_count":0}""";

    internal static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Resources", "Semantica", name));

    internal static byte[] Envelope(string inner, string format = "json") =>
        Encoding.UTF8.GetBytes("{\"format\":" + JsonSerializer.Serialize(format) + ",\"data\":" + JsonSerializer.Serialize(inner) + "}");

    private static string Inner(string entities = "[]", string relationships = "[]", string statistics = EmptyStatistics) =>
        $$"""{"entities":{{entities}},"relationships":{{relationships}},"statistics":{{statistics}}}""";

    [Fact]
    public void Parse_RealFixture_ReadsEveryRecord()
    {
        var export = SemanticaExport.Parse(Fixture("export-graph-json-0.7.0.json"));

        export.Entities.Count.ShouldBe(9);
        export.Relationships.Count.ShouldBe(13);
        export.Entities.Count(entity => entity.Type == "decision").ShouldBe(4);
        export.Entities[0].Id.ShouldBe("db05c33e-6e9e-458c-b6be-d9f3be2c51eb");
        export.Entities[0].RawText.ShouldContain("\"category\": \"storage\"");
        export.Relationships.Single(link => link.Type == "CAUSED").SourceId.ShouldBe("c1d5977e-64f5-4ef8-b067-93bc7fe896f6");
        export.Relationships[^1].Type.ShouldBe("reviewed_by");
        export.Entities.Single(entity => entity.Id == "33da32fb-4d6e-49e0-9dc2-00172691389a")
            .Element.GetProperty("text").GetString().ShouldBe("Zapisać graf w osobnym pliku — 決定");
    }

    [Fact]
    public void Parse_RealMappingFixture_ReadsEveryRecord()
    {
        var mapping = SemanticaMapping.Parse(Fixture("export-graph-json-0.7.0.mapping.json"));

        mapping.SourceNamespace.ShouldBe("semantica-main");
        mapping.LegacyTaskId.ShouldBe("semantica-legacy");
        mapping.EdgeKinds["supports"].ShouldBe("supports");
        mapping.Records.Count.ShouldBe(22);
        mapping.Records["33da32fb-4d6e-49e0-9dc2-00172691389a"].Status.ShouldBe("rejected");
        mapping.Records["33da32fb-4d6e-49e0-9dc2-00172691389a"].TaskId.ShouldBe("storage-task");
        mapping.Records["category_storage"].Status.ShouldBeNull();
    }

    public static TheoryData<string, byte[]> DuplicateKeys => new()
    {
        { "envelope", Encoding.UTF8.GetBytes("{\"format\":\"json\",\"format\":\"json\",\"data\":" + JsonSerializer.Serialize(Inner()) + "}") },
        { "inner data", Envelope($$"""{"entities":[],"entities":[],"relationships":[],"statistics":{{EmptyStatistics}}}""") },
        { "properties", Envelope(Inner("""[{"id":"a","type":"decision","properties":{"outcome":"x","outcome":"y"}}]""",
            statistics: """{"entity_count":1,"relationship_count":0}""")) },
        { "escaped", Envelope($$"""{"entities":[],"entit\u0069es":[],"relationships":[],"statistics":{{EmptyStatistics}}}""") },
    };

    [Theory]
    [MemberData(nameof(DuplicateKeys))]
    public void Parse_DuplicateKey_InEnvelopeInnerDataAndMapping_Refuses(string where, byte[] source)
    {
        where.ShouldNotBeEmpty();
        Should.Throw<ArgumentException>(() => SemanticaExport.Parse(source)).ParamName.ShouldBe("source");
    }

    [Theory]
    [InlineData("""{"sourceNamespace":"a","sourceNamespace":"b","legacyTaskId":"t","records":{}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":{"externalId":"x"},"x":{"externalId":"y"}}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":{"externalId":"x","externalId":"y"}}}""")]
    public void Parse_DuplicateKey_InMapping_Refuses(string json) =>
        Should.Throw<ArgumentException>(() => SemanticaMapping.Parse(Encoding.UTF8.GetBytes(json))).ParamName.ShouldBe("mapping");

    public static TheoryData<string, byte[]> BadText
    {
        get
        {
            var withBadByte = Envelope(Inner("""[{"id":"a","type":"decision","text":"XX"}]""", statistics: """{"entity_count":1,"relationship_count":0}"""));
            var at = withBadByte.AsSpan().IndexOf("XX"u8);
            withBadByte[at] = 0xFF;
            withBadByte[at + 1] = 0xFE;
            return new TheoryData<string, byte[]>
            {
                { "invalid utf-8 byte inside a string", withBadByte },
                { "lone surrogate escape in data", Encoding.UTF8.GetBytes("""{"format":"json","data":"\ud800"}""") },
                { "lone surrogate escape in id", Envelope(Inner("""[{"id":"\ud800","type":"decision"}]""", statistics: """{"entity_count":1,"relationship_count":0}""")) },
                { "lone surrogate escape in type", Envelope(Inner("""[{"id":"a","type":"\ud800"}]""", statistics: """{"entity_count":1,"relationship_count":0}""")) },
                { "utf-8 bom", [0xEF, 0xBB, 0xBF, .. Envelope(Inner())] },
                { "control character in an identifier", Envelope(Inner("""[{"id":"a\u0001b","type":"decision"}]""", statistics: """{"entity_count":1,"relationship_count":0}""")) },
            };
        }
    }

    [Theory]
    [MemberData(nameof(BadText))]
    public void Parse_InvalidUtf8InsideString_RefusesAsArgument(string where, byte[] source)
    {
        where.ShouldNotBeEmpty();
        Should.Throw<ArgumentException>(() => SemanticaExport.Parse(source)).ParamName.ShouldBe("source");
    }

    [Fact]
    public void Parse_InvalidUtf8InsideMapping_RefusesAsArgument()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":{"title":"XX"}}}""");
        var at = bytes.AsSpan().IndexOf("XX"u8);
        bytes[at] = 0xFF;
        bytes[at + 1] = 0xFE;
        Should.Throw<ArgumentException>(() => SemanticaMapping.Parse(bytes)).ParamName.ShouldBe("mapping");
    }

    public static TheoryData<string, byte[]> UnsupportedShapes => new()
    {
        { "format is not json", Envelope(Inner(), format: "yaml") },
        { "envelope extra key", Encoding.UTF8.GetBytes("{\"format\":\"json\",\"data\":" + JsonSerializer.Serialize(Inner()) + ",\"extra\":1}") },
        { "envelope missing data", Encoding.UTF8.GetBytes("""{"format":"json"}""") },
        { "data is not a string", Encoding.UTF8.GetBytes("""{"format":"json","data":{}}""") },
        { "envelope is an array", Encoding.UTF8.GetBytes("[]") },
        { "not json", Encoding.UTF8.GetBytes("nope") },
        { "empty", [] },
        { "normalized shape", Envelope("""{"nodes":[],"edges":[],"decisions":[],"metadata":{}}""") },
        { "inner extra key", Envelope($$"""{"entities":[],"relationships":[],"statistics":{{EmptyStatistics}},"version":"2"}""") },
        { "inner missing statistics", Envelope("""{"entities":[],"relationships":[]}""") },
        { "statistics extra key", Envelope(Inner(statistics: """{"entity_count":0,"relationship_count":0,"nodes":0}""")) },
        { "entity count mismatch", Envelope(Inner(statistics: """{"entity_count":1,"relationship_count":0}""")) },
        { "relationship count mismatch", Envelope(Inner(statistics: """{"entity_count":0,"relationship_count":3}""")) },
        { "count is not a number", Envelope(Inner(statistics: """{"entity_count":"0","relationship_count":0}""")) },
        { "entities is not an array", Envelope($$"""{"entities":{},"relationships":[],"statistics":{{EmptyStatistics}}}""") },
        { "entity without id", Envelope(Inner("""[{"type":"decision"}]""", statistics: """{"entity_count":1,"relationship_count":0}""")) },
        { "entity id is not a string", Envelope(Inner("""[{"id":5,"type":"decision"}]""", statistics: """{"entity_count":1,"relationship_count":0}""")) },
        { "entity is not an object", Envelope(Inner("[1]", statistics: """{"entity_count":1,"relationship_count":0}""")) },
        { "relationship without target", Envelope(Inner(relationships: """[{"id":"r","source_id":"a","type":"depends_on"}]""", statistics: """{"entity_count":0,"relationship_count":1}""")) },
        { "relationship type is not a string", Envelope(Inner(relationships: """[{"id":"r","source_id":"a","target_id":"b","type":null}]""", statistics: """{"entity_count":0,"relationship_count":1}""")) },
        { "padded identifier", Envelope(Inner("""[{"id":" a","type":"decision"}]""", statistics: """{"entity_count":1,"relationship_count":0}""")) },
    };

    [Theory]
    [MemberData(nameof(UnsupportedShapes))]
    public void Parse_UnsupportedShape_Refuses(string reason, byte[] source)
    {
        reason.ShouldNotBeEmpty();
        Should.Throw<ArgumentException>(() => SemanticaExport.Parse(source)).ParamName.ShouldBe("source");
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"legacyTaskId":"t","records":{}}""")]
    [InlineData("""{"sourceNamespace":"a","records":{}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t"}""")]
    [InlineData("""{"sourceNamespace":5,"legacyTaskId":"t","records":{}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{},"schemaVersion":1}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":[]}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":"y"}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":{"externalId":"x","note":"?"}}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":{"externalId":5}}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","records":{"x":{"status":["accepted"]}}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","edgeKinds":[],"records":{}}""")]
    [InlineData("""{"sourceNamespace":"a","legacyTaskId":"t","edgeKinds":{"depends_on":5},"records":{}}""")]
    [InlineData("""{"sourceNamespace":"\ud800","legacyTaskId":"t","records":{}}""")]
    public void Parse_UnsupportedMapping_Refuses(string json) =>
        Should.Throw<ArgumentException>(() => SemanticaMapping.Parse(Encoding.UTF8.GetBytes(json))).ParamName.ShouldBe("mapping");

    [Fact]
    public void Parse_MappingValuesAreKeptAsPlainStrings()
    {
        var mapping = SemanticaMapping.Parse(Encoding.UTF8.GetBytes(
            """{"sourceNamespace":" padded","legacyTaskId":"","records":{"x":{"externalId":" bad id","taskId":"t","status":"nonsense","title":"T"}}}"""));

        mapping.SourceNamespace.ShouldBe(" padded");
        mapping.Records["x"].ExternalId.ShouldBe(" bad id");
        mapping.Records["x"].Status.ShouldBe("nonsense");
        mapping.Records["x"].Title.ShouldBe("T");
        mapping.EdgeKinds.ShouldBeEmpty();
    }
}
