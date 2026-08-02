using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Infrastructure.Rating;

/// <summary>
/// First-party extension: search hits bump the retrieval rating (FR-MEM-1.14). Every hash the
/// search returned gets its access count incremented and rating recomputed in the local meta
/// store; writes record provenance so a later sweep has a rating to judge.
/// </summary>
public sealed class RetrievalRatingExtension : IMemoryExtension
{
    private readonly MetaStore _meta;

    public RetrievalRatingExtension(MetaStore meta) => _meta = meta ?? throw new ArgumentNullException(nameof(meta));

    public string Name => "retrieval-rating";

    public async Task OnWriteAsync(WriteContext context, CancellationToken cancellationToken)
    {
        // The content hash is only known after the store reads the row back, so write-time
        // provenance is not recorded here; the first search hit creates the meta row (FR-MEM-1.14).
        await Task.CompletedTask;
    }

    public async Task OnSearchAsync(SearchContext context, CancellationToken cancellationToken)
    {
        foreach (var result in context.Results)
        {
            await _meta.UpsertAccessAsync(
                context.ProjectId, result.Hash, context: result.Path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task OnDeleteAsync(DeleteContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.Hash))
        {
            await _meta.DeleteAsync(context.ProjectId, context.Hash, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<IReadOnlyList<SweepCandidate>> OnSweepAsync(SweepContext context, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SweepCandidate>>([]);

    public async Task OnConsolidateAsync(ConsolidationContext context, CancellationToken cancellationToken)
        => await Task.CompletedTask;
}
