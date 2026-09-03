namespace AiRaccoon.Core.Memory;

public interface ISharedExtractionRunner
{
    /// <summary>
    ///     Ranks and queues one project's share-worthy entries; the shared index is a per-pass
    ///     input, read once by the caller and reused across projects. Cross-project scoring always
    ///     covers every known project id, fetched here rather than caller-supplied. A minimum score
    ///     admits only rows scoring at or above it — the rest are neither queued nor returned, so
    ///     no review backlog accumulates behind a gated auto-promote pass.
    /// </summary>
    Task<IReadOnlyList<ShareCandidate>> ProposeAsync(
        string projectId,
        SharedIndex sharedIndex,
        bool includeTtlRows,
        int limit,
        double? minScore = null,
        CancellationToken cancellationToken = default);
}
