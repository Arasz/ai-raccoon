namespace AiRaccoon.Core.Memory;

/// <summary>
///     Propose-tier capacity math: the total cap splits into per-project reservations (cap ÷
///     project count); projects may borrow unused space, and eviction fires only over the total cap.
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

    /// <summary>One project's reservation, usage, and whether it is borrowing another project's space.</summary>
    public static PromotionCapacityInfo CapacityFor(int totalCap, int projectCount, int used)
    {
        var reservation = ReservationFor(totalCap, projectCount);
        return new PromotionCapacityInfo(reservation, used, used > reservation);
    }
}

/// <summary>One project's position against its reservation: borrowing = used exceeds reserved.</summary>
public sealed record PromotionCapacityInfo(int Reserved, int Used, bool Borrowing);
