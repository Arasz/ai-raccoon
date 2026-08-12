namespace AiRaccoon.Core.Memory;

public interface ISharedExtractionRunner
{
    /// <summary>
    ///     Ranks and queues one project's share-worthy entries; the shared index is a per-pass
    ///     input, read once by the caller and reused across projects. Cross-project scoring always
    ///     covers every known project id, fetched here rather than caller-supplied.
    /// </summary>
    Task<IReadOnlyList<ShareCandidate>> ProposeAsync(
        string projectId,
        SharedIndex sharedIndex,
        bool includeTtlRows,
        int limit,
        CancellationToken cancellationToken = default);
}
