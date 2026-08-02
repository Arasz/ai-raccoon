using AiRaccon.Core.Common;
using AiRaccon.Core.Degradation;
using AiRaccon.Core.Memory;
using AiRaccon.Core.Rating;
using AiRaccon.Infrastructure.Sqlite;

namespace AiRaccon.Infrastructure.Degradation;

/// <summary>Runs the degradation policy over a project's committed entries; the shared context is sweep-exempt (spec FR-MEM-1.15).</summary>
public sealed class SweepService
{
    private readonly IMemoryStore _store;
    private readonly MetaStore _meta;

    public SweepService(IMemoryStore store, MetaStore meta)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
    }

    public async Task<SweepOutcome> SweepAsync(
        string projectId, double threshold, double ttlDays, bool dryRun, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);

        var projectContext = ContextNaming.ProjectContext(projectId);
        var entries = await _store.ListContextAsync(projectId, projectContext, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var candidates = new List<SweepCandidate>();
        var deleted = new List<string>();

        foreach (var entry in entries)
        {
            var meta = await _meta.GetEntryAsync(projectId, entry.Hash, cancellationToken).ConfigureAwait(false);
            var rating = meta?.Rating ?? RatingPolicy.DefaultBaseScore;
            var ageDays = Math.Max(0, (now - entry.CreatedAt) / 86_400.0);

            if (!DegradationPolicy.ShouldDegrade(rating, ageDays, threshold, ttlDays, meta?.TtlDays))
            {
                continue;
            }

            candidates.Add(new SweepCandidate(entry.Hash, rating, ageDays));
            if (!dryRun)
            {
                await _store.DeleteAsync(projectId, entry.Hash, cancellationToken).ConfigureAwait(false);
                await _meta.DeleteAsync(projectId, entry.Hash, cancellationToken).ConfigureAwait(false);
                deleted.Add(entry.Hash);
            }
        }

        return new SweepOutcome(candidates, deleted);
    }
}
