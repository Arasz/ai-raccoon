using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Decisions;

/// <summary>Selects the newest registered tasks, every task, or explicit task identities.</summary>
public sealed record DecisionTaskSelector
{
    public DecisionTaskSelector(int? last = null, bool all = false, IReadOnlyList<string>? taskIds = null)
    {
        Guard.IsTrue((last.HasValue ? 1 : 0) + (all ? 1 : 0) + (taskIds is not null ? 1 : 0) <= 1,
            nameof(last), "Choose only one of last, all or taskIds.");
        if (last.HasValue)
        {
            Guard.IsInRange(last.Value, 1, 1001, nameof(last));
        }

        if (taskIds is not null)
        {
            Guard.IsInRange(taskIds.Count, 1, 101, nameof(taskIds));
            foreach (var id in taskIds)
            {
                DecisionText.Identifier(id, nameof(taskIds));
            }

            Guard.IsEqualTo(taskIds.Distinct(StringComparer.Ordinal).Count(), taskIds.Count, nameof(taskIds));
        }

        All = all;
        Last = last ?? (!all && taskIds is null ? 1 : null);
        TaskIds = taskIds is null ? null : Array.AsReadOnly(taskIds.ToArray());
    }

    public int? Last { get; }
    public bool All { get; }
    public IReadOnlyList<string>? TaskIds { get; }
}
