using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Rating;

namespace AiRaccoon.Infrastructure.Rating;

/// <summary>
///     First-party extension keeping the rating pipeline wired to on-row columns (P1 rewire):
///     the search hit bump (access_count/last_accessed_at/rating) now happens inside
///     SqliteMemoryStore.SearchAsync, and deletes remove the whole row — so every hook here is a
///     no-op. Kept registered so the extension host architecture (spec §6.2) stays intact for
///     later waves.
/// </summary>
public sealed class RetrievalRatingExtension : IMemoryExtension
{
    public string Name => "retrieval-rating";

    public Task OnWriteAsync(WriteContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnSearchAsync(SearchContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnDeleteAsync(DeleteContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<SweepCandidate>>
        OnSweepAsync(SweepContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SweepCandidate>>([]);

    public Task OnConsolidateAsync(ConsolidationContext context, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
