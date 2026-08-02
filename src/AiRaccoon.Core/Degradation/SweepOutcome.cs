namespace AiRaccoon.Core.Degradation;

/// <summary>Outcome of a degradation sweep; candidates are always reported, deletes only when not dry-run (spec §4.1 memory_sweep).</summary>
public sealed record SweepOutcome(IReadOnlyList<SweepCandidate> Candidates, IReadOnlyList<string> DeletedHashes);
