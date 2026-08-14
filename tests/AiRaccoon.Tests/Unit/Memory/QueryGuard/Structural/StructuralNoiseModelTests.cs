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
///     Parity tests for the shipped structural-noise model (docs/adr/0041): a 200-tree gradient
///     boosted ensemble (`HistGradientBoostingClassifier`, sklearn 1.9.0, max_iter=200,
///     max_depth=4, learning_rate=0.1, random_state=0), trained on the research record's read-path
///     corpus (`gen2/queries.jsonl`, 1815 real queries/excerpts). The fixture's scores come from
///     the exact trained sklearn model's `predict_proba`, not invented — a real reproduction check
///     of the ported tree traversal, in the same spirit as the record's own C#/Python parity check
///     (F7: min Pearson r 0.997).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class StructuralNoiseModelTests
{
    private static readonly IReadOnlyList<FixtureRow> Fixture = LoadFixture();

    [Fact]
    public void Score_IsBetweenZeroAndOne()
    {
        var score = StructuralNoiseModel.Score(StructuralNoiseFeatures.Extract("why did the auth build start failing"));

        score.ShouldBeGreaterThanOrEqualTo(0.0);
        score.ShouldBeLessThanOrEqualTo(1.0);
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
    public void Score_MatchesTheTrainedSklearnModel(string id)
    {
        var row = Fixture.Single(r => r.Id == id);
        var features = StructuralNoiseFeatures.Extract(row.Text);

        var actual = StructuralNoiseModel.Score(features);

        actual.ShouldBe(row.Score, 1e-6, $"score mismatched for fixture '{id}' (family {row.Family})");
    }

    private static IReadOnlyList<FixtureRow> LoadFixture()
    {
        var path = TestData.RepoFile(
            "tests/AiRaccoon.Tests/Unit/Memory/QueryGuard/Structural/assets/model-score-fixture.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<FixtureRow>>(json) ?? [];
    }

    private sealed class FixtureRow
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";

        [JsonPropertyName("family")] public string Family { get; init; } = "";

        [JsonPropertyName("text")] public string Text { get; init; } = "";

        [JsonPropertyName("score")] public double Score { get; init; }
    }
}
