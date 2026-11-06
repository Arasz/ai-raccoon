namespace AiRaccoon.Core.Decisions;

public sealed record DecisionTask(string TaskId, string Title, long RegisteredOrder, DateTimeOffset RegisteredAt, string? SourceRef);
public sealed record DecisionRecord(string DecisionId, string OriginTaskId, int CurrentVersion, DateTimeOffset CreatedAt);
public sealed record DecisionVersion(string DecisionId, int Version, string TaskId, long ProjectRevision,
    DecisionContent Content, DecisionStatus Status, DateTimeOffset RecordedAt, string Actor, string SessionId, string? ChangeReason);
public sealed record DecisionEdge(string EdgeId, string SourceId, string TargetId, DecisionEdgeKind Kind, string Reason,
    string TaskId, long CreatedRevision, DateTimeOffset CreatedAt, string Actor, string SessionId,
    long? RemovedRevision = null, DateTimeOffset? RemovedAt = null, string? RemovalTaskId = null,
    string? RemovalReason = null, string? RemovalActor = null, string? RemovalSessionId = null);
public sealed record DecisionReceipt(string PayloadHash, string ResultJson, long CommittedRevision);
public sealed record DecisionImport(string SourceNamespace, string ExternalId, string? DecisionId, string TaskId,
    string SourceDigest, string RawPayload, string Mapping);
