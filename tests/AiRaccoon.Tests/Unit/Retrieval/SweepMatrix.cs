namespace AiRaccoon.Tests.Unit.Retrieval;

/// <summary>One RRF fusion parameter point: cutoff k and the (fts, vector) weight pair.</summary>
public sealed record SweepPoint(int K, int FtsWeight, int VectorWeight)
{
    public string Id => $"k{K}-w{FtsWeight}{VectorWeight}";
}

/// <summary>
///     The fixed sweep matrix for the RRF fusion search (FR-NM-4): k in {10, 30, 60} x
///     weights {(1,1), (1,2), (2,1)}. Deterministic order — k ascending, then weight pair
///     ascending — so run-to-run comparison is stable.
/// </summary>
public static class SweepMatrix
{
    public static IReadOnlyList<SweepPoint> Points { get; } = Build();

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
}
