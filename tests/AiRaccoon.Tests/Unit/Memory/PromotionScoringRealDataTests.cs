using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Local-only verification against real labeled candidate pools (docs/adr/0018-promotion-scoring-v2.md
///     — gates, candidate-set sizes, and prototype-parity figures are documented there). Fixtures quote
///     private-repo docs and are never committed; set AIRACCOON_SCORING_EVAL_FIXTURE to a manifest JSON
///     file (see <see cref="FixtureManifest" />) declaring one or more fixtures and the gates each must
///     clear — CI has no such file and the test skips.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionScoringRealDataTests(ITestOutputHelper output)
{
    private const string FixtureEnvVar = "AIRACCOON_SCORING_EVAL_FIXTURE";

    [Fact]
    public void ScoresCorrelateWithHandLabeledUsefulness()
    {
        var manifestPath = Environment.GetEnvironmentVariable(FixtureEnvVar);
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            Assert.Skip($"{FixtureEnvVar} not set — local-only verification against the real labeled " +
                        "candidate pool (docs/adr/0018-promotion-scoring-v2.md). Build a fixture with " +
                        "docs/work/promotion-scoring-eval/rebuild_fixture.py, write a manifest declaring " +
                        "it (see FixtureManifest), and point this env var at the manifest — fixtures are " +
                        "never committed to this public repo.");
            return;
        }

        Verify(manifestPath);
    }

    // Gate-machinery tests below: synthetic manifests + fixtures written to a temp directory, run with
    // no env var, so they exercise the manifest/gate machinery itself without the private real-data
    // fixture. Candidate rows use archetype paths whose priors are exact and content-evidence-neutral
    // (docs/adr/0018-promotion-scoring-v2.md's archetype table), so the measured Spearman for a given
    // usefulness assignment is deterministic — verified directly against PromotionScorer, not asserted
    // from memory.

    [Fact]
    public void MinSpearmanGate_ReportsFixtureNameAndBothMeasurements_WhenBelowFloor()
    {
        var dir = CreateTempDir();
        WriteFixture(dir, "reversed.json", CoreRows([4, 3, 2, 1, 0]));
        var manifestPath = WriteManifest(dir, "manifest.json", new
        {
            fixtures = new[] { new { name = "reversed", path = "reversed.json", minSpearman = 0.5 } }
        });

        var ex = Should.Throw<ShouldAssertException>(() => Verify(manifestPath));

        ex.Message.ShouldContain("reversed");
        ex.Message.ShouldContain("-1.000");
        ex.Message.ShouldContain("0.50");
    }

    [Fact]
    public void PrototypeToleranceGate_Fails_WhenMeasuredSpearmanMissesTheBand()
    {
        var dir = CreateTempDir();
        WriteFixture(dir, "forward.json", CoreRows([0, 1, 2, 3, 4]));
        var manifestPath = WriteManifest(dir, "manifest.json", new
        {
            fixtures = new[]
            {
                new { name = "forward", path = "forward.json", prototypeSpearman = 0.0, prototypeTolerance = 0.03 }
            }
        });

        var ex = Should.Throw<ShouldAssertException>(() => Verify(manifestPath));

        ex.Message.ShouldContain("forward");
        ex.Message.ShouldContain("1.000");
        ex.Message.ShouldContain("0.000");
    }

    [Fact]
    public void SubsetGate_FailsIndependently_OfTheFullSetGate()
    {
        var dir = CreateTempDir();
        var rows = CoreRows([0, 1, 2, 3, 4]).Concat(OutlierRows([4, 0, 2], startId: 1001)).ToList();
        WriteFixture(dir, "mixed.json", rows);
        var manifestPath = WriteManifest(dir, "manifest.json", new
        {
            fixtures = new[]
            {
                new
                {
                    name = "mixed", path = "mixed.json", minSpearman = 0.5,
                    subsets = new[] { new { name = "organic", minIdExclusive = 1000, minSpearman = 0.5 } }
                }
            }
        });

        var ex = Should.Throw<ShouldAssertException>(() => Verify(manifestPath));

        ex.Message.ShouldContain("mixed/organic");
        ex.Message.ShouldContain("-0.500");
        ex.Message.ShouldNotContain("mixed: full-set");
    }

    [Fact]
    public void ManifestRelativePath_ResolvesAgainstManifestDirectory()
    {
        var dir = CreateTempDir();
        WriteFixture(dir, "labeled.json", CoreRows([0, 1, 2, 3, 4]));
        var manifestPath = WriteManifest(dir, "manifest.json", new
        {
            fixtures = new[] { new { name = "relative", path = "labeled.json", minSpearman = 0.5 } }
        });

        Should.NotThrow(() => Verify(manifestPath));
    }

    [Fact]
    public void ManifestWithMultipleFixtures_ChecksAllOfThem_NotJustTheFirst()
    {
        var dir = CreateTempDir();
        WriteFixture(dir, "alpha.json", CoreRows([4, 3, 2, 1, 0])); // reversed: fails minSpearman
        WriteFixture(dir, "beta.json", CoreRows([0, 1, 2, 3, 4])); // forward: fails prototype parity
        var manifestPath = WriteManifest(dir, "manifest.json", new
        {
            fixtures = new object[]
            {
                new { name = "alpha", path = "alpha.json", minSpearman = 0.9 },
                new { name = "beta", path = "beta.json", prototypeSpearman = 0.0, prototypeTolerance = 0.03 }
            }
        });

        var ex = Should.Throw<ShouldAssertException>(() => Verify(manifestPath));

        ex.Message.ShouldContain("alpha");
        ex.Message.ShouldContain("beta");
    }

    [Fact]
    public void AllDeclaredGates_Pass_WhenMeasurementsClearEveryThreshold()
    {
        var dir = CreateTempDir();
        WriteFixture(dir, "clean.json", CoreRows([0, 1, 2, 3, 4]));
        var manifestPath = WriteManifest(dir, "manifest.json", new
        {
            fixtures = new[]
            {
                new
                {
                    name = "clean", path = "clean.json", minSpearman = 0.5,
                    prototypeSpearman = 1.0, prototypeTolerance = 0.03,
                    subsets = new[] { new { name = "upper", minIdExclusive = 2, minSpearman = 0.5 } }
                }
            }
        });

        Should.NotThrow(() => Verify(manifestPath));
    }

    /// <summary>Five archetypes whose priors (docs/adr/0018-promotion-scoring-v2.md) are strictly
    /// increasing and, for content shaped like <see cref="NeutralText" />, exactly equal to the
    /// measured score (0.35 / 0.70 / 1.45 / 2.15 / 2.55 — no content-evidence adjustment fires).</summary>
    private static readonly string[] CoreArchetypePaths =
    [
        "docs/README.md", // DocIndex     0.35
        "docs/plans/plan-notes.md", // Plan   0.70
        "docs/reference/api-notes.md", // Reference 1.45
        "docs/explanation/notes.md", // Explanation 2.15
        "docs/adr/0001-decision.md" // Adr    2.55
    ];

    /// <summary>Three more archetypes, priors 0.95 / 1.30 / 2.30, used to build an id-gt-1000 "organic"
    /// subset whose own ranking can be set independently of the core rows'.</summary>
    private static readonly string[] OutlierArchetypePaths =
    [
        "docs/reviews/review-notes.md", // Review   0.95
        "docs/work/notes.md", // WorkNote  1.30
        "docs/charter-doc.md" // Charter   2.30
    ];

    private const string NeutralText =
        "The scheduling component tracks each background job by its identifier and records the queue " +
        "it belongs to along with the time it entered that queue. Downstream consumers read this " +
        "record to decide which jobs to retry and which to archive once processing finishes normally. " +
        "The component keeps no direct reference to the worker that eventually claims a job, relying " +
        "instead on the queue metadata to route work correctly across the cluster nodes involved in " +
        "processing this kind of background task end to end.";

    private static List<(int Id, string SourceFile, int Usefulness)> CoreRows(
        IReadOnlyList<int> usefulness, int startId = 1) =>
        CoreArchetypePaths.Select((path, i) => (startId + i, path, usefulness[i])).ToList();

    private static List<(int Id, string SourceFile, int Usefulness)> OutlierRows(
        IReadOnlyList<int> usefulness, int startId) =>
        OutlierArchetypePaths.Select((path, i) => (startId + i, path, usefulness[i])).ToList();

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "airaccoon-scoring-gate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFixture(
        string dir, string fileName, IEnumerable<(int Id, string SourceFile, int Usefulness)> rows)
    {
        var candidates = rows.Select(r => new
        {
            id = r.Id,
            project_id = "proj-a",
            hash = $"h{r.Id}",
            path = r.SourceFile,
            value = NeutralText,
            source_file = r.SourceFile,
            created_at = 1750000000L,
            access_count = 0,
            rating = 0.5,
            usefulness = r.Usefulness
        });
        File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(candidates));
    }

    private static string WriteManifest(string dir, string fileName, object manifest)
    {
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        return path;
    }

    /// <summary>Runs every fixture a manifest declares, reports every measured Spearman via
    ///     <paramref name="output" /> regardless of pass/fail, then fails once with every gate that
    ///     missed — a later fixture's gates are still checked and reported even if an earlier one failed.</summary>
    private void Verify(string manifestPath)
    {
        var manifest = LoadManifest(manifestPath);
        manifest.Fixtures.ShouldNotBeEmpty($"{manifestPath}: manifest declares no fixtures");

        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? "";
        var failures = new List<string>();
        foreach (var fixture in manifest.Fixtures)
        {
            VerifyFixture(fixture, manifestDir, failures);
        }

        failures.ShouldBeEmpty($"{failures.Count} promotion-scoring gate(s) failed:\n{string.Join("\n", failures)}");
    }

    private void VerifyFixture(FixtureSpec fixture, string manifestDir, List<string> failures)
    {
        var path = ResolvePath(fixture.Path, manifestDir);
        var labeled = LoadLabeled(path);
        labeled.ShouldNotBeEmpty($"{fixture.Name}: fixture at {path} has no rows");

        var allProjectIds = labeled.Select(c => c.ProjectId).Distinct().ToList();
        var scores = labeled.ToDictionary(c => c.Id, c => Score(c, allProjectIds));
        var fullSpearman = Spearman(
            labeled.Select(c => scores[c.Id]).ToList(), labeled.Select(c => (double)c.Usefulness).ToList());
        output.WriteLine(Invariant($"{fixture.Name}: full-set Spearman {fullSpearman:F3} (n={labeled.Count})"));

        if (fixture.MinSpearman is { } minSpearman && fullSpearman < minSpearman)
        {
            failures.Add(Invariant(
                $"{fixture.Name}: full-set Spearman {fullSpearman:F3} below the {minSpearman:F2} gate"));
        }

        if (fixture.PrototypeSpearman is { } prototype)
        {
            fixture.PrototypeTolerance.ShouldNotBeNull(
                $"{fixture.Name}: declares prototypeSpearman but no prototypeTolerance");
            var tolerance = fixture.PrototypeTolerance.Value;
            var delta = Math.Abs(fullSpearman - prototype);
            if (delta > tolerance)
            {
                failures.Add(
                    $"{fixture.Name}: full-set Spearman {fullSpearman.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"misses python prototype parity ({prototype.ToString("F3", CultureInfo.InvariantCulture)} " +
                    $"± {tolerance.ToString("F2", CultureInfo.InvariantCulture)})");
            }
        }

        foreach (var subset in fixture.Subsets ?? [])
        {
            VerifySubset(fixture.Name, subset, labeled, scores, failures);
        }
    }

    private void VerifySubset(
        string fixtureName, SubsetSpec subset, IReadOnlyList<LabeledCandidate> labeled,
        IReadOnlyDictionary<int, double> scores, List<string> failures)
    {
        var subsetCandidates = labeled.Where(c => subset.MinIdExclusive is null || c.Id > subset.MinIdExclusive).ToList();
        if (subsetCandidates.Count == 0)
        {
            failures.Add($"{fixtureName}/{subset.Name}: subset matched no candidates");
            return;
        }

        var subsetSpearman = Spearman(
            subsetCandidates.Select(c => scores[c.Id]).ToList(),
            subsetCandidates.Select(c => (double)c.Usefulness).ToList());
        output.WriteLine(Invariant(
            $"{fixtureName}/{subset.Name}: Spearman {subsetSpearman:F3} (n={subsetCandidates.Count})"));

        if (subset.MinSpearman is { } minSpearman && subsetSpearman < minSpearman)
        {
            failures.Add(Invariant(
                $"{fixtureName}/{subset.Name}: Spearman {subsetSpearman:F3} below the {minSpearman:F2} gate"));
        }
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static FixtureManifest LoadManifest(string path)
    {
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(File.ReadAllText(path), JsonOptions);
        manifest.ShouldNotBeNull($"{path}: failed to parse manifest");
        return manifest;
    }

    private static List<LabeledCandidate> LoadLabeled(string path)
    {
        var labeled = JsonSerializer.Deserialize<List<LabeledCandidate>>(File.ReadAllText(path), JsonOptions);
        labeled.ShouldNotBeNull($"{path}: failed to parse fixture");
        return labeled;
    }

    private static string ResolvePath(string path, string manifestDir) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(manifestDir, path));

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

    /// <summary>One manifest declares every fixture to check and the gate(s) each must clear. Every
    /// field except <see cref="Name" />/<see cref="Path" /> is optional — a fixture declaring only
    /// <see cref="MinSpearman" /> gets only the floor checked.</summary>
    private sealed class FixtureManifest
    {
        [JsonPropertyName("fixtures")]
        public List<FixtureSpec> Fixtures { get; init; } = [];
    }

    private sealed class FixtureSpec
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        /// <summary>Absolute, or resolved relative to the manifest's own directory.</summary>
        [JsonPropertyName("path")]
        public string Path { get; init; } = "";

        [JsonPropertyName("minSpearman")]
        public double? MinSpearman { get; init; }

        [JsonPropertyName("prototypeSpearman")]
        public double? PrototypeSpearman { get; init; }

        [JsonPropertyName("prototypeTolerance")]
        public double? PrototypeTolerance { get; init; }

        [JsonPropertyName("subsets")]
        public List<SubsetSpec>? Subsets { get; init; }
    }

    /// <summary>A named slice of a fixture's rows, filtered by id, with its own gate — generalizes the
    /// v2 organic-subset check (previously a hardcoded `c.Id > 1000` filter).</summary>
    private sealed class SubsetSpec
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("minIdExclusive")]
        public int? MinIdExclusive { get; init; }

        [JsonPropertyName("minSpearman")]
        public double? MinSpearman { get; init; }
    }
}
