using AiRaccoon.Core.Decisions;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Decisions;

public sealed partial class DecisionService
{
    public Task<DecisionResult<DecisionVersion>> TransitionAsync(string projectId, DecisionWrite write, string decisionId,
        int expectedVersion, DecisionStatus targetStatus, string changeReason, CancellationToken cancellationToken = default)
    {
        DecisionText.Required(changeReason, 2000, nameof(changeReason));
        if (targetStatus == DecisionStatus.Superseded)
        {
            throw new DecisionException("decision-invalid-transition", "Use supersede_decision to record the replacement and its permanent edge together.");
        }

        return MutateAsync(projectId, write, "transition", new { decisionId, expectedVersion, targetStatus, changeReason }, async (writer, revision, ct) =>
        {
            var current = await CurrentAsync(writer, decisionId, expectedVersion, ct);
            var version = Revised(current, write, revision, changeReason) with { Status = Transition(current.Status, targetStatus) };
            await writer.AddVersionAsync(version, ct);
            return new Mutation<DecisionVersion>(version);
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionSupersession>> SupersedeAsync(string projectId, DecisionWrite write,
        string replacementId, string replacedId, int expectedReplacementVersion, int expectedReplacedVersion, string reason,
        CancellationToken cancellationToken = default)
    {
        DecisionText.Required(reason, 2000, nameof(reason));
        return MutateAsync(projectId, write, "supersede", new { replacementId, replacedId, expectedReplacementVersion, expectedReplacedVersion, reason }, async (writer, revision, ct) =>
        {
            var replacement = await CurrentAsync(writer, replacementId, expectedReplacementVersion, ct);
            var replaced = await CurrentAsync(writer, replacedId, expectedReplacedVersion, ct);
            if (replacement.Status != DecisionStatus.Accepted || replaced.Status != DecisionStatus.Accepted)
            {
                throw new DecisionException("decision-invalid-transition", "Both replacement and replaced decisions must be accepted.");
            }

            var live = (await writer.EdgesAsync(ct)).Where(edge => edge.RemovedRevision is null).ToArray();
            EnsureCanLink(replacementId, replacedId, DecisionEdgeKind.Supersedes, live);
            var edge = NewEdge(write, revision, replacementId, replacedId, DecisionEdgeKind.Supersedes, reason);
            var superseded = Revised(replaced, write, revision, reason) with { Status = Transition(replaced.Status, DecisionStatus.Superseded) };
            await writer.AddEdgeAsync(edge, ct);
            await writer.AddVersionAsync(superseded, ct);
            return new Mutation<DecisionSupersession>(new DecisionSupersession(replacement, superseded, edge));
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionEdge>> LinkAsync(string projectId, DecisionWrite write, string sourceId,
        string targetId, DecisionEdgeKind kind, string reason, long expectedProjectRevision, CancellationToken cancellationToken = default)
    {
        DecisionText.Identifier(sourceId, nameof(sourceId));
        DecisionText.Identifier(targetId, nameof(targetId));
        DecisionText.Required(reason, 2000, nameof(reason));
        Guard.IsTrue(Enum.IsDefined(kind) && kind != DecisionEdgeKind.Supersedes, nameof(kind));
        if (kind == DecisionEdgeKind.Contradicts && StringComparer.Ordinal.Compare(sourceId, targetId) > 0)
        {
            (sourceId, targetId) = (targetId, sourceId);
        }

        return MutateAsync(projectId, write, "link", new { sourceId, targetId, kind, reason, expectedProjectRevision }, async (writer, revision, ct) =>
        {
            await CurrentAsync(writer, sourceId, null, ct);
            await CurrentAsync(writer, targetId, null, ct);
            var live = (await writer.EdgesAsync(ct)).Where(edge => edge.RemovedRevision is null).ToArray();
            var existing = live.SingleOrDefault(edge => edge.SourceId == sourceId && edge.TargetId == targetId && edge.Kind == kind);
            if (existing is not null)
            {
                if (existing.Reason != reason || existing.TaskId != write.TaskId)
                {
                    throw Conflict("This live link already has different rationale or task provenance.");
                }

                return new Mutation<DecisionEdge>(existing, false);
            }

            await RequireRevisionAsync(writer, expectedProjectRevision, ct);
            EnsureCanLink(sourceId, targetId, kind, live);
            var edge = NewEdge(write, revision, sourceId, targetId, kind, reason);
            await writer.AddEdgeAsync(edge, ct);
            return new Mutation<DecisionEdge>(edge);
        }, cancellationToken);
    }

    public Task<DecisionResult<DecisionEdge>> UnlinkAsync(string projectId, DecisionWrite write, string edgeId,
        string reason, long expectedProjectRevision, CancellationToken cancellationToken = default)
    {
        DecisionText.Identifier(edgeId, nameof(edgeId));
        DecisionText.Required(reason, 2000, nameof(reason));
        return MutateAsync(projectId, write, "unlink", new { edgeId, reason, expectedProjectRevision }, async (writer, revision, ct) =>
        {
            var edge = (await writer.EdgesAsync(ct)).SingleOrDefault(edge => edge.EdgeId == edgeId) ?? throw NotFound("Edge");
            if (edge.Kind == DecisionEdgeKind.Supersedes)
            {
                throw new DecisionException("decision-invalid-transition", "A supersession edge is permanent.");
            }

            if (edge.RemovedRevision is not null)
            {
                throw Conflict("This link is already removed. Read its history or replay the original requestId.");
            }

            await RequireRevisionAsync(writer, expectedProjectRevision, ct);
            var removed = edge with
            {
                RemovedRevision = revision, RemovedAt = Now, RemovalTaskId = write.TaskId,
                RemovalReason = reason, RemovalActor = write.Actor, RemovalSessionId = write.SessionId
            };
            await writer.RemoveEdgeAsync(removed, ct);
            return new Mutation<DecisionEdge>(removed);
        }, cancellationToken);
    }

    private DecisionVersion Revised(DecisionVersion current, DecisionWrite write, long revision, string reason) => current with
    {
        Version = checked(current.Version + 1), TaskId = write.TaskId, ProjectRevision = revision,
        RecordedAt = Now, Actor = write.Actor, SessionId = write.SessionId, ChangeReason = reason
    };

    private DecisionEdge NewEdge(DecisionWrite write, long revision, string sourceId, string targetId, DecisionEdgeKind kind, string reason) =>
        new(Guid.CreateVersion7(Now).ToString(), sourceId, targetId, kind, reason, write.TaskId, revision, Now, write.Actor, write.SessionId);

    private static DecisionStatus Transition(DecisionStatus from, DecisionStatus to)
    {
        try
        {
            return new DecisionState(from).TransitionTo(to).Status;
        }
        catch (InvalidOperationException exception)
        {
            throw new DecisionException("decision-invalid-transition", exception.Message);
        }
    }

    private static void EnsureCanLink(string sourceId, string targetId, DecisionEdgeKind kind, IReadOnlyList<DecisionEdge> live)
    {
        try
        {
            DecisionGraphRules.EnsureCanLink(sourceId, targetId, kind, live.Select(edge => new DecisionLink(edge.SourceId, edge.TargetId, edge.Kind)));
        }
        catch (InvalidOperationException exception)
        {
            throw new DecisionException("decision-invalid-link", exception.Message);
        }
    }

    private static async Task RequireRevisionAsync(IDecisionReader reader, long expectedProjectRevision, CancellationToken cancellationToken)
    {
        Guard.IsGreaterThanOrEqualTo(expectedProjectRevision, 0L, nameof(expectedProjectRevision));
        var current = await reader.RevisionAsync(cancellationToken);
        if (current != expectedProjectRevision)
        {
            throw new DecisionRevisionConflictException(expectedProjectRevision, current);
        }
    }
}
