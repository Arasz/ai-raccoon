namespace AiRaccoon.Core.Degradation;

/// <summary>
///     Outcome of a degradation sweep; candidates are always reported, deletes only when not dry-run (see
///     docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_sweep).
/// </summary>
public sealed record SweepOutcome(IReadOnlyList<SweepCandidate> Candidates, IReadOnlyList<string> DeletedHashes);
