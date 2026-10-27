namespace AiRaccoon.Core.Decisions;

public sealed record DecisionSearchHit(string DecisionId, double Rank);
public sealed record DecisionNode(string DecisionId, string Title, DecisionStatus Status, int CurrentVersion, bool Boundary = false);
public sealed record DecisionTouch(string TaskId, string Reason, int? Version = null, string? EdgeId = null);
public sealed record ExportedDecision(DecisionRecord Record, IReadOnlyList<DecisionVersion> Versions, IReadOnlyList<DecisionTouch> Touches);
public sealed record DecisionExportCounts(int Tasks, int Decisions, int Versions, int Edges, int BoundaryNodes, int Imports);
public sealed record DecisionExport(int SchemaVersion, DateTimeOffset ExportedAt, DecisionTaskSelector Selector,
    IReadOnlyList<string> SelectedTaskIds, IReadOnlyList<DecisionTask> Tasks, IReadOnlyList<ExportedDecision> Decisions,
    IReadOnlyList<DecisionEdge> Edges, IReadOnlyList<DecisionNode> BoundaryNodes, IReadOnlyList<DecisionTask> ReferencedTasks,
    IReadOnlyList<DecisionImport> ImportRecords, DecisionExportCounts Counts);
public sealed record DecisionMatch(DecisionNode Decision, string Category, string Outcome, string Reasoning,
    IReadOnlyList<DecisionSource> Sources, double? Rank = null);
public sealed record DecisionQueryResult(IReadOnlyList<DecisionMatch> Decisions, int TotalMatched, bool HasMore,
    string? NextAfterId, string Method);
public sealed record DecisionQuery(string? Query = null, string? Category = null, IReadOnlyList<DecisionStatus>? Statuses = null,
    DecisionTaskSelector? Selector = null, int Limit = 20, string? AfterId = null);
public enum DecisionDirection { Upstream, Downstream, Both }
public sealed record DecisionGraphRequest(string DecisionId, DecisionDirection Direction = DecisionDirection.Upstream,
    int Depth = 2, int MaxNodes = 100, bool IncludeContext = false);
public sealed record DecisionGraph(IReadOnlyList<DecisionNode> Nodes, IReadOnlyList<DecisionEdge> Edges,
    bool Truncated, IReadOnlyList<string> Reasons, int? OmittedNodes, int? OmittedEdges);
public sealed record DecisionPreviewOptions(DecisionTaskSelector? Selector = null, int MaxDecisions = 20,
    int MaxEdges = 30, int MaxChars = 12000);
public sealed record DecisionPreviewCounts(int Tasks, int Decisions, int BoundaryNodes, int Edges);
public sealed record DecisionPreview(string Text, DecisionPreviewCounts Total, DecisionPreviewCounts Shown,
    DecisionPreviewCounts Omitted, int AbbreviatedFields);
