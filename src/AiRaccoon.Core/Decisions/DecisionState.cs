using System.Text.Json.Serialization;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Decisions;

[JsonConverter(typeof(DecisionEnumConverter<DecisionStatus>))]
public enum DecisionStatus
{
    Proposed,
    Accepted,
    Rejected,
    Superseded,
    Deprecated
}

/// <summary>A decision's immutable lifecycle state; transitions return a new value.</summary>
public sealed record DecisionState
{
    public DecisionState(DecisionStatus status)
    {
        Guard.IsTrue(Enum.IsDefined(status), nameof(status));
        Status = status;
    }

    public DecisionStatus Status { get; }

    /// <summary>True for the statuses an import may create directly; superseded needs its replacement edge.</summary>
    public static bool Importable(DecisionStatus status) => status is DecisionStatus.Proposed or DecisionStatus.Accepted or DecisionStatus.Rejected or DecisionStatus.Deprecated;

    public void EnsureCanAmend()
    {
        if (Status is not (DecisionStatus.Proposed or DecisionStatus.Accepted))
        {
            ThrowHelper.ThrowInvalidOperationException($"Cannot amend a {Status} decision.");
        }
    }

    public DecisionState TransitionTo(DecisionStatus target)
    {
        var allowed = (Status, target) is
            (DecisionStatus.Proposed, DecisionStatus.Accepted or DecisionStatus.Rejected) or
            (DecisionStatus.Accepted, DecisionStatus.Superseded or DecisionStatus.Deprecated);
        if (!allowed)
        {
            ThrowHelper.ThrowInvalidOperationException($"Cannot transition from {Status} to {target}.");
        }

        return new DecisionState(target);
    }
}
