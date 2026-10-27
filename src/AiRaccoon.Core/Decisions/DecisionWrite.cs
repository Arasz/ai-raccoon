namespace AiRaccoon.Core.Decisions;

/// <summary>Task and retry provenance for one committed decision command.</summary>
public sealed record DecisionWrite
{
    public DecisionWrite(string taskId, string sessionId, string requestId, string? actor = null)
    {
        DecisionText.Identifier(taskId, nameof(taskId));
        DecisionText.Identifier(sessionId, nameof(sessionId));
        DecisionText.Identifier(requestId, nameof(requestId));
        if (actor is not null)
        {
            DecisionText.Identifier(actor, nameof(actor));
        }

        TaskId = taskId;
        SessionId = sessionId;
        RequestId = requestId;
        Actor = actor ?? sessionId;
    }

    public string TaskId { get; }
    public string SessionId { get; }
    public string RequestId { get; }
    public string Actor { get; }
}

public sealed record DecisionResult<T>(string ProjectId, long ProjectRevision, T Value, bool Replayed = false);
public sealed record DecisionDetails(DecisionRecord Record, IReadOnlyList<DecisionVersion> Versions, int SelectedVersion = 0);
public sealed record DecisionSupersession(DecisionVersion Replacement, DecisionVersion Replaced, DecisionEdge Edge);

/// <summary>A decision command refusal with a stable machine-readable kind.</summary>
public sealed class DecisionException(string kind, string message, int? currentVersion = null, long? currentProjectRevision = null) : Exception(message)
{
    public string Kind { get; } = kind;
    public int? CurrentVersion { get; } = currentVersion;
    public long? CurrentProjectRevision { get; } = currentProjectRevision;
}
