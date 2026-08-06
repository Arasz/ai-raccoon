namespace AiRaccoon.Core.Memory;

/// <summary>Whether the extraction proposes candidates or promotes them into the shared tier.</summary>
public enum ExtractMode
{
    Propose = 0,
    Promote = 1
}

/// <summary>One project-scoped row considered for sharing (pool filtered by the store: project scope, embedded).</summary>
public sealed record ExtractionCandidateRow(
    string Hash,
    string Path,
    string Value,
    string? SourceFile,
    double Rating,
    int AccessCount,
    DateTimeOffset CreatedAt,
    int? TtlDays);

/// <summary>A ranked candidate with the mechanical reasons it scored.</summary>
public sealed record ShareCandidate(
    string Hash,
    string Path,
    string ValuePreview,
    double Rating,
    int AccessCount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Reasons);

/// <summary>Extraction outcome: ranked candidates; in promote mode the hashes actually promoted.</summary>
public sealed record ShareExtractResult(
    IReadOnlyList<ShareCandidate> Candidates,
    IReadOnlyList<string> PromotedHashes);
