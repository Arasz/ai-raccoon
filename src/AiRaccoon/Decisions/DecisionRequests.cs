using AiRaccoon.Core.Decisions;

namespace AiRaccoon.Decisions;

/// <summary>
///     The decision inputs both adapters build: the HTTP routes bind them from the body, the MCP
///     tools from their parameters, and each sends itself to the same service call.
/// </summary>
internal static class DecisionRequests
{
    /// <summary>Refuses a blank or padded project id before the gate could resolve one from the working directory.</summary>
    public static string Explicit(string projectId)
    {
        DecisionText.Identifier(projectId, nameof(projectId));
        return projectId;
    }
}

internal record WriteRequest(string TaskId, string SessionId, string RequestId, string? Actor = null)
{
    public DecisionWrite Write() => new(TaskId, SessionId, RequestId, Actor);
}

internal sealed record RegisterTaskRequest(string TaskId, string SessionId, string RequestId, string Title, string? SourceRef = null, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionTask>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.RegisterTaskAsync(projectId, Write(), Title, SourceRef, ct);
}

internal sealed record RecordRequest(string TaskId, string SessionId, string RequestId, string Title, string Category, string Scenario, string Outcome, string Reasoning,
    DecisionStatus Status = DecisionStatus.Proposed, double? Confidence = null, string? Maker = null, IReadOnlyList<string>? Alternatives = null,
    IReadOnlyList<string>? Consequences = null, IReadOnlyList<DecisionSource>? Sources = null, DateTimeOffset? ValidFrom = null, DateTimeOffset? ValidUntil = null, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionVersion>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.RecordAsync(projectId, Write(), new DecisionContent(Title, Category, Scenario, Outcome, Reasoning, Confidence, Maker,
            Alternatives, Consequences, Sources, ValidFrom, ValidUntil), Status, ct);
}

internal sealed record AmendRequest(string TaskId, string SessionId, string RequestId, int ExpectedVersion, string ChangeReason, DecisionContent Content, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionVersion>> SendAsync(IDecisionService service, string projectId, string decisionId, CancellationToken ct) =>
        service.AmendAsync(projectId, Write(), decisionId, ExpectedVersion, ChangeReason, Content, ct);
}

internal sealed record TransitionRequest(string TaskId, string SessionId, string RequestId, int ExpectedVersion, DecisionStatus TargetStatus, string ChangeReason, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionVersion>> SendAsync(IDecisionService service, string projectId, string decisionId, CancellationToken ct) =>
        service.TransitionAsync(projectId, Write(), decisionId, ExpectedVersion, TargetStatus, ChangeReason, ct);
}

internal sealed record SupersedeRequest(string TaskId, string SessionId, string RequestId, string ReplacementId, string ReplacedId,
    int ExpectedReplacementVersion, int ExpectedReplacedVersion, string Reason, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionSupersession>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.SupersedeAsync(projectId, Write(), ReplacementId, ReplacedId, ExpectedReplacementVersion, ExpectedReplacedVersion, Reason, ct);
}

internal sealed record LinkRequest(string TaskId, string SessionId, string RequestId, string SourceId, string TargetId, DecisionEdgeKind Kind,
    string Reason, long ExpectedProjectRevision, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionEdge>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.LinkAsync(projectId, Write(), SourceId, TargetId, Kind, Reason, ExpectedProjectRevision, ct);
}

internal sealed record UnlinkRequest(string TaskId, string SessionId, string RequestId, string Reason, long ExpectedProjectRevision, string? Actor = null)
    : WriteRequest(TaskId, SessionId, RequestId, Actor)
{
    public Task<DecisionResult<DecisionEdge>> SendAsync(IDecisionService service, string projectId, string edgeId, CancellationToken ct) =>
        service.UnlinkAsync(projectId, Write(), edgeId, Reason, ExpectedProjectRevision, ct);
}

internal sealed record PrecedentsRequest(string Scenario, string? Category = null, int Limit = 5, bool IncludeInactive = false)
{
    public Task<DecisionResult<DecisionQueryResult>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.PrecedentsAsync(projectId, Scenario, Category, Limit, IncludeInactive, ct);
}

internal sealed record SelectionRequest(DecisionTaskSelector? Selector = null)
{
    public Task<DecisionResult<DecisionExport>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.ExportAsync(projectId, Selector, ct);
}

internal sealed record ImportPreviewRequest(byte[] Source, byte[] Mapping)
{
    public Task<DecisionResult<DecisionImportReport>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.PreviewSemanticaImportAsync(projectId, Source, Mapping, ct);
}

internal sealed record ImportApplyRequest(string SessionId, string RequestId, byte[] Source, byte[] Mapping,
    string SourceDigest, string MappingDigest, long ExpectedProjectRevision, string? Actor = null)
{
    public Task<DecisionResult<DecisionImportOutcome>> SendAsync(IDecisionService service, string projectId, CancellationToken ct) =>
        service.ApplySemanticaImportAsync(projectId, new DecisionImportCommand(SessionId, RequestId, Source, Mapping,
            SourceDigest, MappingDigest, ExpectedProjectRevision, Actor), ct);
}
