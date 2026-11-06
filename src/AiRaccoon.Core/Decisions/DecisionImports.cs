namespace AiRaccoon.Core.Decisions;

/// <summary>Size and report limits for the Semantica import.</summary>
public static class DecisionImportLimits
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxIssues = 200;
}

/// <summary>An import apply request: the exact file bytes plus the digests and revision the preview reported.</summary>
public sealed record DecisionImportCommand(string SessionId, string RequestId, byte[] Source, byte[] Mapping,
    string SourceDigest, string MappingDigest, long ExpectedProjectRevision, string? Actor = null);

public sealed record DecisionImportIssue(string Code, string Path, string Message, string? RecordId = null);

public sealed record DecisionImportCounts(int Entities, int Relationships, int Decisions, int Edges,
    int Retained, int AlreadyImported, int Collapsed);

public sealed record DecisionImportReport(string SourceDigest, string MappingDigest, bool CanApply, DecisionImportCounts Counts,
    IReadOnlyList<DecisionImportIssue> Blockers, int OmittedBlockers,
    IReadOnlyList<DecisionImportIssue> Notices, int OmittedNotices);

public sealed record DecisionImportOutcome(string SourceDigest, DecisionImportCounts Counts);
