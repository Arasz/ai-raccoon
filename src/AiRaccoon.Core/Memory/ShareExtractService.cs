using FluentValidation;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     The share-extract pipeline (docs/adr/0065). Lived as 51 body lines inside
///     `ShareTools.ShareExtract`, in a class whose own doc comment says "no business logic here" —
///     so the CLI and the background extraction loop could not reach it and none of it could be
///     unit-tested without standing up an MCP server.
/// </summary>
public sealed class ShareExtractService(
    IMemoryStore store,
    ISharedExtractionRunner extraction,
    IPromotionQueue queue) : IShareExtractService
{
    private static readonly ShareExtractRequestValidator Validator = new();

    public async Task<ShareExtractResult> RunAsync(ShareExtractRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await Validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.AutoPromote && !request.Confirm)
        {
            throw new ConfirmationRequiredException(
                "autoPromote shares candidates with ALL projects — pass confirm=true to enable");
        }

        var limit = request.Limit ?? SharedExtractionService.DefaultCandidateLimit;

        return request.Promotes
            ? await PromoteAsync(request, limit, cancellationToken).ConfigureAwait(false)
            : await ProposeAsync(request, limit, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ShareExtractResult> PromoteAsync(ShareExtractRequest request, int limit,
        CancellationToken cancellationToken)
    {
        var outcome = await queue.PromoteAsync([.. request.ProjectIds], limit, cancellationToken)
            .ConfigureAwait(false);
        return new ShareExtractResult([], outcome.PromotedHashes)
        {
            SkippedDuplicates = outcome.SkippedDuplicates,
            Absorbed = outcome.Absorbed,
            Failures = outcome.Failures
        };
    }

    /// <summary>The shared index is read once and reused across projects — a per-pass input, not per project.</summary>
    private async Task<ShareExtractResult> ProposeAsync(ShareExtractRequest request, int limit,
        CancellationToken cancellationToken)
    {
        var sharedIndex = await store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<ShareCandidate>();
        foreach (var projectId in request.ProjectIds)
        {
            candidates.AddRange(await extraction
                .ProposeAsync(projectId, sharedIndex, request.IncludeTtlRows, limit, cancellationToken)
                .ConfigureAwait(false));
        }

        return new ShareExtractResult(candidates, []);
    }
}
