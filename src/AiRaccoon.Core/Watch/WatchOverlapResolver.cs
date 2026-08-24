using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Core.Watch;

/// <summary>One registration candidate for overlap resolution: literal path + creation order.</summary>
public sealed record WatchOverlapCandidate(string Path, long CreatedAt);

/// <summary>One pruned watch: its path, and the surviving watch that covers it.</summary>
public sealed record PrunedWatch(string Path, string CoveredBy);

/// <summary>The result of resolving one candidate add against a project's existing registrations.</summary>
public enum WatchOverlapOutcome
{
    /// <summary>Nothing else in the project contains the candidate; existing entries the
    /// candidate contains are listed in <see cref="WatchOverlapDecision.Pruned" />.</summary>
    Accepted,

    /// <summary>The candidate is contained by (or loses the tie-break against) an existing
    /// watch; nothing is written. <see cref="WatchOverlapDecision.CoveringPath" /> names it.</summary>
    Rejected,

    /// <summary>The candidate's literal path exactly matches an existing registration — a no-op.</summary>
    Idempotent
}

/// <summary>The decision for adding one candidate to a project's watch set.</summary>
public sealed record WatchOverlapDecision(
    WatchOverlapOutcome Outcome,
    string? CoveringPath,
    IReadOnlyList<PrunedWatch> Pruned);

/// <summary>
///     No-overlapping-watches policy (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.2/§5.5): the watch whose scope contains another wins. Containment is
///     <see cref="IngestPath.IsWithinScope" /> (separator-aware, symlink-real-path-resolved).
///     Mutual containment (two registrations that resolve to the same real path under different
///     literal spellings — symlinks, host-case-insensitive duplicates) is tie-broken by the
///     longest literal path, then by earliest <see cref="WatchOverlapCandidate.CreatedAt" />.
///     One implementation, two call sites: the runtime add path
///     (<c>WatchService.AddAsync</c>/<c>WatchStore.PruneAndAddAsync</c>) and the v11 ladder
///     migration both resolve through this same type.
/// </summary>
public interface IWatchOverlapResolver
{
    /// <summary>
    ///     Within one project's registrations, the outermost-watch prune set: every registration
    ///     contained by another survivor (after the mutual-containment tie-break) is pruned.
    /// </summary>
    IReadOnlyList<PrunedWatch> SelectPruned(IReadOnlyList<WatchOverlapCandidate> registrations);

    /// <summary>
    ///     Decides what adding <paramref name="candidate" /> to <paramref name="existing" />
    ///     (assumed already overlap-free) does.
    /// </summary>
    WatchOverlapDecision Resolve(IReadOnlyList<WatchOverlapCandidate> existing, WatchOverlapCandidate candidate);
}

/// <inheritdoc cref="IWatchOverlapResolver" />
public sealed class WatchOverlapResolver : IWatchOverlapResolver
{
    public IReadOnlyList<PrunedWatch> SelectPruned(IReadOnlyList<WatchOverlapCandidate> registrations)
    {
        if (registrations.Count < 2)
        {
            return [];
        }

        var pruned = new List<PrunedWatch>();

        // Step 1: real-path-equal groups (mutual containment via symlink/case spellings) reduce
        // to one survivor per group — longest literal path, then earliest registration.
        var groups = registrations
            .GroupBy(r => IngestPath.ResolveReal(r.Path), IngestPath.PathComparer)
            .ToArray();

        var candidates = new List<WatchOverlapCandidate>(groups.Length);
        foreach (var group in groups)
        {
            var ordered = group
                .OrderByDescending(r => r.Path.Length)
                .ThenBy(r => r.CreatedAt)
                .ToArray();
            var survivor = ordered[0];
            candidates.Add(survivor);
            foreach (var loser in ordered.Skip(1))
            {
                pruned.Add(new PrunedWatch(loser.Path, survivor.Path));
            }
        }

        // Step 2: cross-group containment — keep only the outermost watches; a candidate covered
        // by another is named by the deepest (most specific) surviving ancestor that contains it.
        var finalSurvivors = candidates
            .Where(c => candidates.All(other => ReferenceEquals(other, c) ||
                                                !IngestPath.IsWithinScope(c.Path, other.Path)))
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (finalSurvivors.Contains(candidate))
            {
                continue;
            }

            var coveredBy = finalSurvivors
                .Where(s => IngestPath.IsWithinScope(candidate.Path, s.Path))
                .OrderByDescending(s => s.Path.Length)
                .First();
            pruned.Add(new PrunedWatch(candidate.Path, coveredBy.Path));
        }

        return pruned;
    }

    public WatchOverlapDecision Resolve(IReadOnlyList<WatchOverlapCandidate> existing, WatchOverlapCandidate candidate)
    {
        var exactMatch = existing.FirstOrDefault(e => IngestPath.PathComparer.Equals(e.Path, candidate.Path));
        if (exactMatch is not null)
        {
            return new WatchOverlapDecision(WatchOverlapOutcome.Idempotent, exactMatch.Path, []);
        }

        var combined = new List<WatchOverlapCandidate>(existing.Count + 1) { candidate };
        combined.AddRange(existing);
        var pruned = SelectPruned(combined);

        var candidatePruned = pruned.FirstOrDefault(p => IngestPath.PathComparer.Equals(p.Path, candidate.Path));
        if (candidatePruned is not null)
        {
            return new WatchOverlapDecision(WatchOverlapOutcome.Rejected, candidatePruned.CoveredBy, []);
        }

        return new WatchOverlapDecision(WatchOverlapOutcome.Accepted, null, pruned);
    }
}
