using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Decisions;

public enum DecisionEdgeKind { DependsOn, Supersedes, Supports, Contradicts }

public sealed record DecisionLink(string SourceId, string TargetId, DecisionEdgeKind Kind);

/// <summary>Checks a proposed link against the live typed decision graph.</summary>
public static class DecisionGraphRules
{
    public static void EnsureCanLink(string sourceId, string targetId, DecisionEdgeKind kind,
        IEnumerable<DecisionLink> liveLinks)
    {
        DecisionText.Identifier(sourceId, nameof(sourceId));
        DecisionText.Identifier(targetId, nameof(targetId));
        Guard.IsNotNull(liveLinks);
        Guard.IsTrue(Enum.IsDefined(kind), nameof(kind));
        if (sourceId == targetId)
        {
            ThrowHelper.ThrowInvalidOperationException("A decision cannot link to itself.");
        }

        if (kind is not (DecisionEdgeKind.DependsOn or DecisionEdgeKind.Supersedes))
        {
            return;
        }

        var outgoing = liveLinks.Where(link => link.Kind == kind).ToLookup(link => link.SourceId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(targetId);
        while (pending.TryPop(out var current))
        {
            if (current == sourceId)
            {
                ThrowHelper.ThrowInvalidOperationException($"The {kind} link would create a cycle.");
            }

            if (seen.Add(current))
            {
                foreach (var link in outgoing[current])
                {
                    pending.Push(link.TargetId);
                }
            }
        }
    }
}
