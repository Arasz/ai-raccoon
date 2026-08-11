using System.ComponentModel;

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
    int? TtlDays,
    string? SourceType = null);

/// <summary>A ranked candidate with the mechanical reasons it scored. Only Score and Reasons fed
/// the ranking — Rating/AccessCount are informational context v3 does not read (ADR-0018).</summary>
public sealed record ShareCandidate(
    string Hash,
    string Path,
    string ValuePreview,
    double Score,
    [property: Description("Informational only — not a scoring input.")] double Rating,
    [property: Description("How many search result sets this hash appeared in — not a usage count, and not a scoring input.")] int AccessCount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Reasons,
    string? SourceFile);

/// <summary>Extraction outcome: ranked candidates; in promote mode the hashes actually created
/// (promoted), chunks folded into already-represented files (absorbed), and value twins skipped.</summary>
public sealed record ShareExtractResult(
    IReadOnlyList<ShareCandidate> Candidates,
    IReadOnlyList<string> PromotedHashes)
{
    public int SkippedDuplicates { get; init; }
    public int Absorbed { get; init; }
    public IReadOnlyList<PromoteFailure> Failures { get; init; } = [];
}

/// <summary>Dedup index over the existing shared tier.</summary>
public sealed record SharedIndex(IReadOnlyList<string> Values, IReadOnlyList<string> Paths);
