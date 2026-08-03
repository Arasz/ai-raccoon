using AiRaccoon.Core.Common;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Infrastructure.Degradation;

/// <summary>Runs the degradation policy over a project's committed entries; the shared context is sweep-exempt (spec FR-MEM-1.15).</summary>
public sealed class SweepService(IMemoryStore store, MetaStore meta)
{
    private readonly MetaStore _meta = meta ?? throw new ArgumentNullException(nameof(meta));
    private readonly IMemoryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<SweepOutcome> SweepAsync(
        string projectId, double threshold, double ttlDays, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var projectContext = ContextNaming.ProjectContext(projectId);
        var entries = await _store.ListContextAsync(projectId, projectContext, cancellationToken).ConfigureAwait(false);

        // Shared rows are path-scoped (distinct hashes), but guard anyway: an entry whose
        // content also lives in the shared tier must never be swept out of it (FR-MEM-1.15).
        var sharedEntries = await _store.ListContextAsync(projectId, ContextNaming.SharedContext, cancellationToken)
            .ConfigureAwait(false);
        var sharedHashes = sharedEntries.Select(e => e.Hash).ToHashSet(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidates = new List<SweepCandidate>();
        var deleted = new List<string>();

        foreach (var entry in entries)
        {
            if (sharedHashes.Contains(entry.Hash))
            {
                continue;
            }

            var meta = await _meta.GetEntryAsync(projectId, entry.Hash, cancellationToken).ConfigureAwait(false);
            var rating = meta?.Rating ?? RatingPolicy.DefaultBaseScore;
            var ageDays = Math.Max(0, (now - entry.CreatedAt) / 86_400.0);

            if (!DegradationPolicy.ShouldDegrade(rating, ageDays, threshold, ttlDays, meta?.TtlDays))
            {
                continue;
            }

            candidates.Add(new SweepCandidate(entry.Hash, rating, ageDays));
            if (dryRun)
            {
                continue;
            }

            await _store.DeleteAsync(projectId, entry.Hash, cancellationToken).ConfigureAwait(false);
            await _meta.DeleteAsync(projectId, entry.Hash, cancellationToken).ConfigureAwait(false);
            deleted.Add(entry.Hash);
        }

        return new SweepOutcome(candidates, deleted);
    }
}
