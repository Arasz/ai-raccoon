using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Core.Memory.QueryGuard.Structural;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard.Structural;

/// <summary>
///     Parity and shape tests for the structural/lexical noise-feature extractor (docs/adr/0041).
///     30 shape statistics: the research record's 31 (`gen2/features.py`) minus `compress_ratio`
///     (F5/F7 — dropping it costs nothing in AUC/recall and removes the only allocating step, p50
///     0.141ms to 0.107ms). The fixture is real corpus text scored by the exact Python reference
///     implementation (`gen2/features.py`), the same one behind every number in the research
///     record — not invented input.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class StructuralNoiseFeaturesTests
{
    private static readonly IReadOnlyList<FixtureRow> Fixture = LoadFixture();

    [Fact]
    public void Extract_WithEmptyString_ReturnsAllZeros()
    {
        var result = StructuralNoiseFeatures.Extract(string.Empty);

        result.Length.ShouldBe(StructuralNoiseFeatures.FeatureCount);
        result.ShouldAllBe(v => v == 0.0);
    }

    [Fact]
    public void Extract_ReturnsExactlyThirtyFeatures()
    {
        StructuralNoiseFeatures.Extract("some ordinary text").Length.ShouldBe(30);
    }

    public static TheoryData<string> FixtureIds
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var id in Fixture.Select(r => r.Id))
            {
                data.Add(id);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FixtureIds))]
    public void Extract_MatchesThePythonReferenceImplementation(string id)
    {
        var row = Fixture.Single(r => r.Id == id);

        var actual = StructuralNoiseFeatures.Extract(row.Text);

        actual.Length.ShouldBe(row.F30.Count);
        for (var i = 0; i < actual.Length; i++)
        {
            actual[i].ShouldBe(row.F30[i], 1e-6, $"feature index {i} mismatched for fixture '{id}' (family {row.Family})");
        }
    }

    private static IReadOnlyList<FixtureRow> LoadFixture()
    {
        var path = TestData.RepoFile(
            "tests/AiRaccoon.Tests/Unit/Memory/QueryGuard/Structural/assets/feature-parity-fixture.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<FixtureRow>>(json) ?? [];
    }

    private sealed class FixtureRow
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";

        [JsonPropertyName("family")] public string Family { get; init; } = "";

        [JsonPropertyName("text")] public string Text { get; init; } = "";

        [JsonPropertyName("f30")] public List<double> F30 { get; init; } = [];
    }
}
