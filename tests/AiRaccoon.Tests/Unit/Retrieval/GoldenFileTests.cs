using System.Runtime.InteropServices;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class GoldenFileTests
{
    public const string RegenerateEnvVar = "AIRACCOON_HARNESS_REGENERATE_GOLDEN";

    /// <summary>
    ///     Gate: a fresh reference run must reproduce the committed golden top-k exactly (same
    ///     hashes in the same order, rankings within 1e-6). Set AIRACCOON_HARNESS_REGENERATE_GOLDEN=1
    ///     (or run scripts/regenerate-retrieval-golden.py) to rewrite the golden file.
    /// </summary>
    [Fact]
    public async Task GoldenFile_MatchesFreshReferenceRun()
    {
        // The golden is an osx-arm64 artifact: its hashes and rankings come from that host's
        // ONNX output (docs/work/archive/2026-08-03-native-memory-plan.md). Comparing it on
        // another platform reports every row as different — 680 of them on ubuntu — which is a
        // platform fact, not a regression. Skip loudly rather than leave the only unfiltered
        // gate permanently red; the structural check below still runs everywhere.
        var diagnosticOnly = !OperatingSystem.IsMacOS() ||
                             RuntimeInformation.ProcessArchitecture != Architecture.Arm64;

        var run = await ReferenceRunCache.GetAsync();
        var goldenPath = Path.Combine(ReferenceAssets.AssetsDirectory, GoldenFile.FileName);
        var regenerate = Environment.GetEnvironmentVariable(RegenerateEnvVar) == "1";

        if (regenerate)
        {
            var regenerated = GoldenFile.FromRun(run) with { Path = goldenPath };
            regenerated.Save();
            return; // regeneration run itself was validated by the runner + smoke gates
        }

        var golden = GoldenFile.Load(goldenPath);
        golden.SchemaVersion.ShouldBe(GoldenFile.CurrentSchemaVersion);
        golden.Queries.Count.ShouldBe(run.ResultsByQuery.Count);

        var differences = golden.Differences(run);
        if (diagnosticOnly)
        {
            Console.WriteLine($"GOLDENDIAG host={RuntimeInformation.RuntimeIdentifier} count={differences.Count}");
            Console.WriteLine($"GOLDENDIAG engine golden={golden.Engine} run={run.Engine}");
            Console.WriteLine($"GOLDENDIAG model golden={golden.Model} run={run.Model}");
            foreach (var d in differences.Take(12))
            {
                Console.WriteLine("GOLDENDIAG " + d);
            }

            Assert.Skip("diagnostic run: see GOLDENDIAG lines");
        }

        differences.ShouldBeEmpty(
            "the committed golden reference is stale; regenerate with scripts/regenerate-retrieval-golden.py");
    }

    [Fact]
    public void CommittedGoldenFile_EveryHitCarriesHashRankingPathAndSnippet()
    {
        var golden = GoldenFile.Load(Path.Combine(ReferenceAssets.AssetsDirectory, GoldenFile.FileName));

        golden.Queries.ShouldNotBeEmpty();
        foreach (var query in golden.Queries)
        {
            query.Id.ShouldNotBeNullOrWhiteSpace();
            foreach (var hit in query.Hits)
            {
                hit.Hash.ShouldNotBeNullOrWhiteSpace();
                hit.Path.ShouldNotBeNullOrWhiteSpace();
                hit.Snippet.ShouldNotBeNullOrWhiteSpace();
                hit.Ranking.ShouldBeInRange(0.0, 1.0);
            }
        }
    }
}
