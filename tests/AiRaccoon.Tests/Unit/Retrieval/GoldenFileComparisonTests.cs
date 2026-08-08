using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>
///     Portable ranking comparison (ADR-0015): cross-platform GGUF inference moves rankings by
///     1e-4..3e-3 and swaps near-ties across the top-k boundary, none of which is a regression.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class GoldenFileComparisonTests
{
    private const string Engine = "sqlite-memory-1.3.5+sqlite-vector-1.0.0";
    private const string Model = "model.gguf";

    [Fact]
    public void Differences_RankingsShiftedWithinTolerance_IsEmpty()
    {
        var golden = MakeGolden(MakeQuery("Q1",
            MakeHit("h1", 0.900), MakeHit("h2", 0.800), MakeHit("h3", 0.700)));
        var run = MakeRun(MakeResult("Q1",
            MakeHit("h1", 0.902), MakeHit("h2", 0.802), MakeHit("h3", 0.702)));

        golden.Differences(run).ShouldBeEmpty();
    }

    [Fact]
    public void Differences_AdjacentNearTieSwap_IsEmpty()
    {
        var golden = MakeGolden(MakeQuery("Q1",
            MakeHit("h1", 0.9005), MakeHit("h2", 0.9000)));
        var run = MakeRun(MakeResult("Q1",
            MakeHit("h2", 0.9002), MakeHit("h1", 0.9001)));

        golden.Differences(run).ShouldBeEmpty();
    }

    [Fact]
    public void Differences_KBoundarySubstitutionWithinTolerance_IsEmpty()
    {
        var golden = MakeGolden(MakeQuery("Q1",
            MakeHit("h1", 0.900), MakeHit("h2", 0.800), MakeHit("h3", 0.700)));
        var run = MakeRun(MakeResult("Q1",
            MakeHit("h1", 0.900), MakeHit("h2", 0.800), MakeHit("h4", 0.698)));

        golden.Differences(run).ShouldBeEmpty();
    }

    [Fact]
    public void Differences_HashReplacedFarFromBoundary_ReportsDifference()
    {
        var golden = MakeGolden(MakeQuery("Q1",
            MakeHit("h1", 0.900), MakeHit("h2", 0.800), MakeHit("h3", 0.700)));
        var run = MakeRun(MakeResult("Q1",
            MakeHit("h1", 0.900), MakeHit("h4", 0.850), MakeHit("h3", 0.700)));

        golden.Differences(run).ShouldNotBeEmpty();
    }

    [Fact]
    public void Differences_RankingMovedBeyondTolerance_ReportsDifference()
    {
        var golden = MakeGolden(MakeQuery("Q1", MakeHit("h1", 0.900)));
        var run = MakeRun(MakeResult("Q1", MakeHit("h1", 0.910)));

        golden.Differences(run).ShouldNotBeEmpty();
    }

    [Fact]
    public void Differences_EngineDiffers_ReportsDifference()
    {
        var golden = MakeGolden(MakeQuery("Q1", MakeHit("h1", 0.900)));
        var run = MakeRun(MakeResult("Q1", MakeHit("h1", 0.900))) with { Engine = "other-engine" };

        golden.Differences(run).ShouldNotBeEmpty();
    }

    [Fact]
    public void Differences_ModelDiffers_ReportsDifference()
    {
        var golden = MakeGolden(MakeQuery("Q1", MakeHit("h1", 0.900)));
        var run = MakeRun(MakeResult("Q1", MakeHit("h1", 0.900))) with { Model = "other-model.gguf" };

        golden.Differences(run).ShouldNotBeEmpty();
    }

    private static GoldenHit MakeHit(string hash, double ranking) => new(hash, ranking, $"path/{hash}.md", $"snippet-{hash}");

    private static ReferenceHit MakeReferenceHit(string hash, double ranking, int seq) => new(hash, seq, ranking, $"path/{hash}.md", $"snippet-{hash}");

    private static GoldenQuery MakeQuery(string id, params GoldenHit[] hits) => new(id, $"text-{id}", hits);

    private static ReferenceQueryResult MakeResult(string id, params GoldenHit[] hits) =>
        new(id, $"text-{id}", [.. hits.Select((h, i) => MakeReferenceHit(h.Hash, h.Ranking, i))]);

    private static GoldenFile MakeGolden(params GoldenQuery[] queries) =>
        new(GoldenFile.CurrentSchemaVersion, Engine, Model, "sha256", "harness", 10, 0.0, 1, queries)
        { Path = "unused" };

    private static ReferenceRun MakeRun(params ReferenceQueryResult[] results) =>
        new(Engine, Model, "harness", 10, 0.0, 1, results);
}
