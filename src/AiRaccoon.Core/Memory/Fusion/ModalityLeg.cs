namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>
///     One retrieval leg's contribution to a hybrid search: whether it was queried at all, and the
///     candidates it returned in rank order. A skipped or degraded leg is not a leg that disagrees
///     (docs/adr/0078). Wraps the caller's list rather than copying it, so the default path pays
///     nothing to construct one.
/// </summary>
public sealed record ModalityLeg(string Name, bool Queried, IReadOnlyList<MemorySearchResult> Candidates)
{
    /// <summary>Only a leg that ran and returned candidates holds an opinion the fusion can regress against.</summary>
    public bool Contributes => Queried && Candidates.Count > 0;

    /// <summary>A leg that never ran — no engine, a zero weight, or an empty query expression.</summary>
    public static ModalityLeg Skipped(string name) => new(name, false, []);

    /// <summary>A leg that ran; an empty result is degradation or a miss, never a vote against every candidate.</summary>
    public static ModalityLeg From(string name, IReadOnlyList<MemorySearchResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return new ModalityLeg(name, true, candidates);
    }
}
