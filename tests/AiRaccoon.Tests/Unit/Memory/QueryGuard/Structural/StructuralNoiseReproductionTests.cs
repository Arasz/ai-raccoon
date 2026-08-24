using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Core.Memory.QueryGuard.Structural;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard.Structural;

/// <summary>
///     Acceptance criterion 1 (docs/adr/0041): does the shipped C# artifact — the real
///     <see cref="StructuralNoiseFeatures" /> extractor feeding the real
///     <see cref="StructuralNoiseModel" /> ensemble — reproduce the research record's headline
///     (recall ≈ 0.573 at ≈1% FPR, AUC ≈ 0.964) on data the shipped model never trained on?
///     <para>
///     The 274-row fixture is a genuinely held-out 15% of the research record's read-path corpus
///     (`gen2/queries.jsonl`), split by family with seed 1234 <b>before</b> the shipped model was
///     fit (`scripts/train-structural-noise-model.py`) — not a re-run of the record's own
///     leave-one-family-out protocol (that number is reported separately in docs/adr/0041 from the
///     Python side), but an end-to-end check of the actual thing that ships: real text in,
///     C#-ported features, C#-ported trees, out.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class StructuralNoiseReproductionTests
{
    // scripts/train-structural-noise-model.py, 5-fold out-of-fold calibration on the 85% training
    // split only (never touching this held-out 15%), target FPR 1% — the record's own headline
    // operating point (docs/adr/0041 §Measurement).
    private const double OnePercentTargetThreshold = 0.9961473492522555;

    private static readonly HeldOutFixture Fixture = LoadFixture();

    [Fact]
    public void ShippedModel_OnGenuinelyHeldOutData_ReproducesTheHeadlineWithinTolerance()
    {
        var scores = Fixture.Rows
            .Select(r => (r.Cls, Score: StructuralNoiseModel.Score(StructuralNoiseFeatures.Extract(r.Text))))
            .ToList();

        var noise = scores.Where(s => s.Cls == 1).Select(s => s.Score).ToList();
        var signal = scores.Where(s => s.Cls == 0).Select(s => s.Score).ToList();

        var recall = noise.Count(s => s >= OnePercentTargetThreshold) / (double)noise.Count;
        var fpr = signal.Count(s => s >= OnePercentTargetThreshold) / (double)signal.Count;
        var auc = Auc(noise, signal);

        // Record headline (docs/adr/0041): recall 0.573 @ ~1% FPR, AUC 0.964. Measured here on the
        // shipped C# artifact against 274 genuinely held-out rows (174 noise / 100 signal) — a much
        // smaller sample than the record's 24-family cross-validation, so the tolerance is wide
        // enough to absorb that sample-size noise, not to hide a real mismatch.
        recall.ShouldBeGreaterThanOrEqualTo(0.5, $"recall was {recall:F3} (record: 0.573)");
        auc.ShouldBeGreaterThanOrEqualTo(0.90, $"AUC was {auc:F3} (record: 0.964)");
        fpr.ShouldBeLessThanOrEqualTo(0.05, $"FPR was {fpr:F3} (target ~1%)");
    }

    /// <summary>Mann-Whitney rank-sum AUC — no external stats dependency needed for one test.</summary>
    private static double Auc(IReadOnlyList<double> positive, IReadOnlyList<double> negative)
    {
        var ranked = positive.Select(s => (Score: s, IsPositive: true))
            .Concat(negative.Select(s => (Score: s, IsPositive: false)))
            .OrderBy(t => t.Score)
            .ToList();

        double rankSum = 0;
        var i = 0;
        while (i < ranked.Count)
        {
            var j = i;
            while (j < ranked.Count && ranked[j].Score == ranked[i].Score)
            {
                j++;
            }

            var avgRank = (i + 1 + j) / 2.0;
            for (var k = i; k < j; k++)
            {
                if (ranked[k].IsPositive)
                {
                    rankSum += avgRank;
                }
            }

            i = j;
        }

        var n1 = positive.Count;
        var n2 = negative.Count;
        return (rankSum - n1 * (n1 + 1) / 2.0) / (n1 * n2);
    }

    private static HeldOutFixture LoadFixture()
    {
        var path = TestData.RepoFile(
            "tests/AiRaccoon.Tests/Unit/Memory/QueryGuard/Structural/assets/held-out-reproduction-fixture.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HeldOutFixture>(json) ?? new HeldOutFixture();
    }

    private sealed class HeldOutFixture
    {
        [JsonPropertyName("rows")] public List<FixtureRow> Rows { get; init; } = [];
    }

    private sealed class FixtureRow
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";

        [JsonPropertyName("family")] public string Family { get; init; } = "";

        [JsonPropertyName("cls")] public int Cls { get; init; }

        [JsonPropertyName("text")] public string Text { get; init; } = "";
    }
}
