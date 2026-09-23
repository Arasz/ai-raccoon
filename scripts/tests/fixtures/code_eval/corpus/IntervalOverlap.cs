namespace CodeEvalFixture;

/// <summary>Closed-interval line-range overlap (E3 fixture corpus: csharp/small).</summary>
public static class IntervalOverlap
{
    /// <summary>True iff [startA, endA] and [startB, endB] share at least one integer.</summary>
    public static bool Overlaps(int startA, int endA, int startB, int endB) =>
        startA <= endB && startB <= endA;

    /// <summary>The number of shared integers between the two closed intervals, or 0.</summary>
    public static int SharedLength(int startA, int endA, int startB, int endB)
    {
        if (!Overlaps(startA, endA, startB, endB))
        {
            return 0;
        }

        var start = Math.Max(startA, startB);
        var end = Math.Min(endA, endB);
        return end - start + 1;
    }
}
