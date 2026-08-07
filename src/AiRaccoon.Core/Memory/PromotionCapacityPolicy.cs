namespace AiRaccoon.Core.Memory;

/// <summary>
///     Propose-tier capacity math: the total cap is split into per-project reservations
///     (cap ÷ project count), projects may borrow each other's unused space, and eviction
///     fires only when the queue is over the total cap. Pure functions — the store feeds
///     counts, the policy decides.
/// </summary>
public static class PromotionCapacityPolicy
{
    /// <summary>Floor(totalCap / projectCount); 0 when nothing can be guaranteed (no projects, zero cap, or more projects than slots).</summary>
    public static int ReservationFor(int totalCap, int projectCount)
    {
        if (projectCount <= 0 || totalCap <= 0)
        {
            return 0;
        }

        var reservation = totalCap / projectCount;
        return reservation >= 1 ? reservation : 0;
    }

    /// <summary>True when the queue is over the total cap and an eviction is due.</summary>
    public static bool NeedsEviction(int totalCount, int totalCap) => totalCount > totalCap;

    /// <summary>Per-project reservation, usage, and whether it is borrowing another project's space.</summary>
    public static IReadOnlyDictionary<string, PromotionCapacityInfo> CapacityInfo(
        int totalCap, int projectCount, IReadOnlyDictionary<string, int> perProjectCounts)
    {
        var reservation = ReservationFor(totalCap, projectCount);
        return perProjectCounts.ToDictionary(
            pair => pair.Key,
            pair => new PromotionCapacityInfo(reservation, pair.Value, pair.Value > reservation),
            StringComparer.Ordinal);
    }
}

/// <summary>
///     One project's position against its reservation: borrowing = used exceeds reserved.
///     Shipped on the wire as-is (via PromotionMeta) — three primitives with no behavior, so a
///     DTO/mapper pair was not worth adding; a wire-only shape can still split off later if this
///     record ever needs to diverge (see #118).
/// </summary>
public sealed record PromotionCapacityInfo(int Reserved, int Used, bool Borrowing);
