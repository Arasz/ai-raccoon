using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Local-only verification against the real labeled candidate pool from the 3-agent scoring
///     evaluation (docs/adr/0018-promotion-scoring-v2.md). The fixture quotes private-repo docs, so it
///     is never committed here — set AIRACCOON_SCORING_EVAL_FIXTURE to one or more local JSON paths
///     (Path.PathSeparator-delimited) to run this locally; CI has no such file and the test skips.
///
///     Gate is picked from the data, not the filename: a fixture with any id > 1000 is the v2 set (61
///     original + 86 organic backup-slice entries) and is held to full-set Spearman >= 0.45 and
///     organic-only-subset (id > 1000) Spearman >= 0.50; a fixture with none is the v1 61-candidate set,
///     held to full-set Spearman >= 0.60.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionScoringRealDataTests
{
    private const string FixtureEnvVar = "AIRACCOON_SCORING_EVAL_FIXTURE";
    private const double V1FullSetFloor = 0.60;
    private const double V2FullSetFloor = 0.45;
    private const double V2OrganicSubsetFloor = 0.50;

    [Fact]
    public void ScoresCorrelateWithHandLabeledUsefulness()
    {
        var raw = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.Skip($"{FixtureEnvVar} not set — local-only verification against the real labeled " +
                        "candidate pool (docs/adr/0018-promotion-scoring-v2.md); the fixture is never " +
                        "committed to this public repo.");
            return;
        }

        foreach (var path in raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            Verify(path);
        }
    }

    private static void Verify(string path)
    {
        var labeled = JsonSerializer.Deserialize<List<LabeledCandidate>>(
            File.ReadAllText(path), JsonOptions);
        labeled.ShouldNotBeNull();
        labeled.ShouldNotBeEmpty();

        var allProjectIds = labeled.Select(c => c.ProjectId).Distinct().ToList();
        var scores = labeled.ToDictionary(c => c.Id, c => Score(c, allProjectIds));

        var isV2 = labeled.Any(c => c.Id > 1000);
        var fullSpearman = Spearman(
            labeled.Select(c => scores[c.Id]).ToList(), labeled.Select(c => (double)c.Usefulness).ToList());

        if (!isV2)
        {
            fullSpearman.ShouldBeGreaterThanOrEqualTo(V1FullSetFloor,
                $"{path}: v1 full-set Spearman {fullSpearman:F3} below the {V1FullSetFloor:F2} gate");
            return;
        }

        fullSpearman.ShouldBeGreaterThanOrEqualTo(V2FullSetFloor,
            $"{path}: v2 full-set Spearman {fullSpearman:F3} below the {V2FullSetFloor:F2} gate");

        var organicOnly = labeled.Where(c => c.Id > 1000).ToList();
        var organicSpearman = Spearman(
            organicOnly.Select(c => scores[c.Id]).ToList(), organicOnly.Select(c => (double)c.Usefulness).ToList());
        organicSpearman.ShouldBeGreaterThanOrEqualTo(V2OrganicSubsetFloor,
            $"{path}: v2 organic-subset Spearman {organicSpearman:F3} below the {V2OrganicSubsetFloor:F2} gate");
    }

    private static double Score(LabeledCandidate candidate, IReadOnlyList<string> allProjectIds)
    {
        var row = new ExtractionCandidateRow(
            candidate.Hash, candidate.Path, candidate.Value, candidate.SourceFile, candidate.Rating,
            candidate.AccessCount, DateTimeOffset.FromUnixTimeSeconds(candidate.CreatedAt), null);
        var (score, _) = PromotionScorer.Score(row, candidate.ProjectId, allProjectIds);
        return score;
    }

    /// <summary>Average-rank Spearman, matching promotion-scoring-eval/eval.py's rank() so the C#
    /// number is directly comparable to the Python-measured figures in the ADR.</summary>
    private static double Spearman(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var rx = AverageRanks(xs);
        var ry = AverageRanks(ys);
        var mx = rx.Average();
        var my = ry.Average();
        var num = rx.Zip(ry, (a, b) => (a - mx) * (b - my)).Sum();
        var denX = rx.Sum(a => (a - mx) * (a - mx));
        var denY = ry.Sum(b => (b - my) * (b - my));
        var den = Math.Sqrt(denX * denY);
        return den == 0 ? 0.0 : num / den;
    }

    private static double[] AverageRanks(IReadOnlyList<double> values)
    {
        var order = Enumerable.Range(0, values.Count).OrderBy(i => values[i]).ToArray();
        var ranks = new double[values.Count];
        var i = 0;
        while (i < order.Length)
        {
            var j = i;
            while (j + 1 < order.Length && values[order[j + 1]] == values[order[i]])
            {
                j++;
            }

            var avgRank = (i + j) / 2.0 + 1;
            for (var k = i; k <= j; k++)
            {
                ranks[order[k]] = avgRank;
            }

            i = j + 1;
        }

        return ranks;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class LabeledCandidate
    {
        [JsonPropertyName("id")]
        public int Id { get; init; }

        [JsonPropertyName("project_id")]
        public string ProjectId { get; init; } = "";

        [JsonPropertyName("hash")]
        public string Hash { get; init; } = "";

        [JsonPropertyName("path")]
        public string Path { get; init; } = "";

        [JsonPropertyName("value")]
        public string Value { get; init; } = "";

        [JsonPropertyName("source_file")]
        public string? SourceFile { get; init; }

        [JsonPropertyName("created_at")]
        public long CreatedAt { get; init; }

        [JsonPropertyName("access_count")]
        public int AccessCount { get; init; }

        [JsonPropertyName("rating")]
        public double Rating { get; init; }

        [JsonPropertyName("usefulness")]
        public int Usefulness { get; init; }
    }
}
