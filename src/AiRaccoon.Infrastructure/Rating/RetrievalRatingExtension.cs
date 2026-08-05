using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Rating;

namespace AiRaccoon.Infrastructure.Rating;

/// <summary>
///     First-party extension keeping the rating pipeline wired to on-row columns: the search hit
///     bump now happens inside SqliteMemoryStore.SearchAsync and deletes remove the whole row, so
///     every hook here is a no-op — kept registered to preserve the extension-host architecture (see docs/work/features-agent-memory/spec-issue-1.md §6.2).
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
