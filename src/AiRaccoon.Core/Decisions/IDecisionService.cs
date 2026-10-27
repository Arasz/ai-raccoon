namespace AiRaccoon.Core.Decisions;

/// <summary>Project decision registration and auditable graph changes.</summary>
public interface IDecisionService
{
    Task<DecisionResult<DecisionTask>> RegisterTaskAsync(string projectId, DecisionWrite write, string title, string? sourceRef = null, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionVersion>> RecordAsync(string projectId, DecisionWrite write, DecisionContent content, DecisionStatus status = DecisionStatus.Proposed, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionVersion>> AmendAsync(string projectId, DecisionWrite write, string decisionId, int expectedVersion, string changeReason, DecisionContent content, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionVersion>> TransitionAsync(string projectId, DecisionWrite write, string decisionId, int expectedVersion, DecisionStatus targetStatus, string changeReason, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionSupersession>> SupersedeAsync(string projectId, DecisionWrite write, string replacementId, string replacedId, int expectedReplacementVersion, int expectedReplacedVersion, string reason, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionEdge>> LinkAsync(string projectId, DecisionWrite write, string sourceId, string targetId, DecisionEdgeKind kind, string reason, long expectedProjectRevision, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionEdge>> UnlinkAsync(string projectId, DecisionWrite write, string edgeId, string reason, long expectedProjectRevision, CancellationToken cancellationToken = default);
    Task<DecisionResult<DecisionDetails>> GetAsync(string projectId, string decisionId, int? version = null, bool includeHistory = false, CancellationToken cancellationToken = default);
}
