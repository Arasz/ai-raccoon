using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;

namespace AiRaccoon.Infrastructure.Degradation;

/// <summary>
///     Runs the degradation policy over a project's committed entries; the shared context is sweep-exempt (see
///     docs/work/features-agent-memory/spec-issue-1.md, FR-MEM-1.15). Ratings and TTLs are read on-row — the meta database is gone.
/// </summary>
public sealed class SweepService(IMemoryStore store, TimeProvider timeProvider) : ISweepService
{
    public async Task<SweepOutcome> SweepAsync(
        string projectId, double threshold, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var projectContext = ContextNaming.ProjectContext(projectId);
        var entries = await store.ListContextAsync(projectId, projectContext, cancellationToken).ConfigureAwait(false);

        // Shared rows are path-scoped (distinct hashes), but guard anyway: an entry whose
        // content also lives in the shared tier must never be swept out of it (see docs/work/features-agent-memory/spec-issue-1.md, FR-MEM-1.15).
        var sharedEntries = await store.ListContextAsync(projectId, ContextNaming.SharedContext, cancellationToken)
            .ConfigureAwait(false);
        var sharedHashes = sharedEntries.Select(e => e.Hash).ToHashSet(StringComparer.Ordinal);

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var candidates = new List<SweepCandidate>();
        var deleted = new List<string>();

        foreach (var entry in entries)
        {
            if (sharedHashes.Contains(entry.Hash))
            {
                continue;
            }

            var metadata = await store.GetMetadataAsync(projectId, entry.Hash, cancellationToken)
                .ConfigureAwait(false);
            var rating = metadata?.Rating ?? RatingPolicy.DefaultBaseScore;
            var ageDays = Math.Max(0, (now - entry.CreatedAt) / 86_400.0);

            // Only entries with an explicit per-entry TTL degrade (global ttl knob removed).
            if (!DegradationPolicy.ShouldDegrade(rating, ageDays, threshold, metadata?.TtlDays))
            {
                continue;
            }

            candidates.Add(new SweepCandidate(entry.Hash, rating, ageDays));
            if (dryRun)
            {
                continue;
            }

            // Scoped to 'project' (H2): entries was enumerated from the project context only, so
            // the delete must not also remove a sibling row sharing this hash in another scope.
            await store.DeleteInScopeAsync(projectId, entry.Hash, "project", cancellationToken)
                .ConfigureAwait(false);
            deleted.Add(entry.Hash);
        }

        return new SweepOutcome(candidates, deleted);
    }
}
