using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>
///     One RRF fusion parameter point: cutoff k, the (fts, vector) weight pair, the
///     minScore filter, and the candidate-window policy. MinRelativeScore and Window default to
///     the pre-sweep values so the FR-NM-4 (see docs/work/features-native-memory/native-memory.feature) grid points keep their original meaning.
/// </summary>
public sealed record SweepPoint(
    int K,
    int FtsWeight,
    int VectorWeight,
    double MinRelativeScore = 0.0,
    CandidateWindowMode Window = CandidateWindowMode.Max3X100)
{
    public string Id => $"k{K}-w{FtsWeight}{VectorWeight}";
}

/// <summary>
///     The fixed sweep matrix for the RRF fusion search (FR-NM-4; see docs/work/features-native-memory/native-memory.feature): k in {10, 30, 60} x
///     weights {(1,1), (1,2), (2,1)}. Deterministic order — k ascending, then weight pair
///     ascending — so run-to-run comparison is stable.
/// </summary>
public static class SweepMatrix
{
    public static IReadOnlyList<SweepPoint> Points { get; } = Build();

    /// <summary>
    ///     Wave 4 RRF parameter grid (docs/plans/retrieval-improvement-c.md §3, Wave 4): k in {10, 30, 60, 120}
    ///     x weights {(1,1), (1,2), (2,1)} x minScore {0.0, 0.3, 0.5, 0.7} x window {Max3x100, Max5x50},
    ///     in deterministic ascending order (k, weight pair, minScore, window).
    /// </summary>
    public static IReadOnlyList<SweepPoint> RrfGrid { get; } = BuildRrfGrid();

    private static IReadOnlyList<SweepPoint> Build()
    {
        var points = new List<SweepPoint>();
        foreach (var k in new[] { 10, 30, 60 })
        {
            foreach (var (fts, vector) in new[] { (1, 1), (1, 2), (2, 1) })
            {
                points.Add(new SweepPoint(k, fts, vector));
            }
        }

        return points;
    }

    private static IReadOnlyList<SweepPoint> BuildRrfGrid()
    {
        var points = new List<SweepPoint>();
        foreach (var k in new[] { 10, 30, 60, 120 })
        {
            foreach (var (fts, vector) in new[] { (1, 1), (1, 2), (2, 1) })
            {
                foreach (var minScore in new[] { 0.0, 0.3, 0.5, 0.7 })
                {
                    foreach (var window in new[] { CandidateWindowMode.Max3X100, CandidateWindowMode.Max5X50 })
                    {
                        points.Add(new SweepPoint(k, fts, vector, minScore, window));
                    }
                }
            }
        }

        return points;
    }
}
