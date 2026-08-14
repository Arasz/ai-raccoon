using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory.QueryGuard;

/// <summary>
///     Detects a memory_search query that is itself machine output rather than a question
///     (docs/adr/0040). Pure string/regex checks only — no embedding, no I/O — so this runs before
///     the 14.6-26.5 ms embedding cost a real query would otherwise pay.
/// </summary>
public static partial class QueryGuardPolicy
{
    public const string RefusePolicyName = "MachineOutputQuery";
    public const string WarnPolicyName = "LogLikeQueryContent";

    public const string RefuseGuidance =
        "This looks like tool output rather than a question. Memory search matches meaning, so search for what you want to find — e.g. 'why did the auth build start failing' rather than pasting the build log.";

    public const string WarnGuidance =
        "This query contains what looks like log or stack-trace output. Memory search matches meaning, not literal text — if you're not intentionally searching for this exact fragment, try rephrasing as a question about what you want to find.";

    // Refuse tier — unambiguous machine-output shapes. Measured read-only against the live bank's
    // search_quality table (2026-08-14): every graded query matching either shape scored 2/5, never
    // higher — "[IMPORTANT: Background process...]" 12/12, "[ASYNC DELEGATION BATCH COMPLETE...]"
    // 10/10. The first shape mirrors AiRaccoon.Core.Memory.Filtering.Policies.HermesProcessNoisePolicy
    // (the write-path filter, ADR-0029) by design — same evidence, same structural signature.
    private const string HermesProcessNotificationPrefix = "[IMPORTANT: Background process";
    private const string HermesProcessNotificationCompletedMarker = "completed normally";
    private const string HermesProcessNotificationCommandMarker = "Command:";
    private const string AsyncDelegationBatchPrefix = "[ASYNC DELEGATION BATCH COMPLETE";
    private const string AsyncDelegationBatchMarker = "dispatched earlier has finished";

    // Warn tier — structural markers of genuine log/tool output, not merely a long or multi-line
    // query: the dominant source of low grades in this bank's traffic is long rambling task briefs
    // (agents pasting their own instructions as the query), not log content, so length/newline
    // alone was measured and rejected as a signal — it would mislabel that majority case as
    // "tool output," which is false. These two markers are backed by a real graded query
    // (search_quality id 251, grade 1/5: a pasted HttpRequestException stack trace).
    [GeneratedRegex(@"(?m)^[ \t]*at\s+\S+\(", RegexOptions.None)]
    private static partial Regex StackFrameLineRegex();

    [GeneratedRegex(@"(?m)^(?:trce|dbug|info|warn|fail|crit):\s", RegexOptions.None)]
    private static partial Regex ConsoleLogLevelPrefixRegex();

    public static QueryGuardVerdict Evaluate(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var trimmed = query.TrimStart();

        if (IsHermesProcessNotification(trimmed) || IsAsyncDelegationBatchNotification(trimmed))
        {
            return QueryGuardVerdict.Refuse(RefusePolicyName, RefuseGuidance);
        }

        if (StackFrameLineRegex().IsMatch(query) || ConsoleLogLevelPrefixRegex().IsMatch(query))
        {
            return QueryGuardVerdict.Warn(WarnPolicyName, WarnGuidance);
        }

        return QueryGuardVerdict.Clean;
    }

    private static bool IsHermesProcessNotification(string trimmed) =>
        trimmed.StartsWith(HermesProcessNotificationPrefix, StringComparison.OrdinalIgnoreCase) &&
        trimmed.Contains(HermesProcessNotificationCompletedMarker, StringComparison.OrdinalIgnoreCase) &&
        trimmed.Contains(HermesProcessNotificationCommandMarker, StringComparison.OrdinalIgnoreCase);

    private static bool IsAsyncDelegationBatchNotification(string trimmed) =>
        trimmed.StartsWith(AsyncDelegationBatchPrefix, StringComparison.OrdinalIgnoreCase) &&
        trimmed.Contains(AsyncDelegationBatchMarker, StringComparison.OrdinalIgnoreCase);
}
