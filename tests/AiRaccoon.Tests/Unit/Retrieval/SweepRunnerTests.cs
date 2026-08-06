using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Retrieval;

public sealed class SweepMatrixTests
{
    [Fact]
    public void Matrix_ContainsAllNineParameterPoints()
    {
        var points = SweepMatrix.Points;

        points.Count.ShouldBe(9);
        points.Select(p => (p.K, p.FtsWeight, p.VectorWeight)).ShouldBe(
        [
            (10, 1, 1), (10, 1, 2), (10, 2, 1),
            (30, 1, 1), (30, 1, 2), (30, 2, 1),
            (60, 1, 1), (60, 1, 2), (60, 2, 1)
        ]);
    }

    [Fact]
    public void Matrix_IsDeterministicAcrossReads() => SweepMatrix.Points.Select(p => p.Id).ShouldBe(SweepMatrix.Points.Select(p => p.Id));

    [Fact]
    public void RrfGrid_ContainsAllNinetySixParameterPoints()
    {
        var points = SweepMatrix.RrfGrid;

        points.Count.ShouldBe(96);
        points.Distinct().Count().ShouldBe(96, "every k x weight x minScore x window combination must appear once");
        points[0].ShouldBe(new SweepPoint(10, 1, 1, 0.0, CandidateWindowMode.Max3X100));
        points[^1].ShouldBe(new SweepPoint(120, 2, 1, 0.7, CandidateWindowMode.Max5X50));
        points.Select(p => (p.K, p.FtsWeight, p.VectorWeight, p.MinScore, p.Window)).ShouldBeInOrder();
    }

    [Fact]
    public void RrfGrid_IsDeterministicAcrossReads() => SweepMatrix.RrfGrid.ShouldBe(SweepMatrix.RrfGrid);
}

public sealed class SweepRunnerTests
{
    [Fact]
    public async Task RunAsync_RecordsOutcomePerMatrixPoint_WithPerfectRanking()
    {
        var queries = new List<CorpusQuery>
        {
            new("q1", "query one", ["d1", "d2"]),
            new("q2", "query two", ["d3"])
        };

        var outcomes = await SweepRunner.RunAsync(queries, (_, query, _) =>
                Task.FromResult<IReadOnlyList<string>>(
                    [.. query.RelevantDocIds, "irrelevant-1", "irrelevant-2", "irrelevant-3"]),
            TestContext.Current.CancellationToken);

        outcomes.Count.ShouldBe(9);
        outcomes.Select(o => o.Point.Id).ShouldBe(SweepMatrix.Points.Select(p => p.Id));

        foreach (var outcome in outcomes)
        {
            outcome.NdcgAt10.ShouldBe(1.0);
            outcome.NdcgAt20.ShouldBe(1.0);
            outcome.Mrr.ShouldBe(1.0);
            outcome.RecallAt10.ShouldBe(1.0);
            outcome.RecallAt30.ShouldBe(1.0);
        }
    }

    [Fact]
    public async Task RunAsync_NoRelevantHits_RecordsZeroes()
    {
        var queries = new List<CorpusQuery> { new("q1", "query one", ["d1"]) };

        var outcomes = await SweepRunner.RunAsync(queries, (_, _, _) =>
                Task.FromResult<IReadOnlyList<string>>(["x", "y", "z"]),
            TestContext.Current.CancellationToken);

        var outcome = outcomes[0];
        outcome.NdcgAt10.ShouldBe(0.0);
        outcome.Mrr.ShouldBe(0.0);
        outcome.RecallAt10.ShouldBe(0.0);
    }
}
