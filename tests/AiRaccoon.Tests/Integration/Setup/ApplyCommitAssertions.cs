namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     The honest claim an --apply verb's bank writes must satisfy (ADR-0075 §5.3): the CLI's
///     synchronous call commits only its outbox request; any domain-table mutation is performed
///     later by the maintenance loop's relay job, which stamps <c>maintenance_jobs</c> in the same
///     pass. Exact equality on the changed set cannot hold on a loaded machine — the 15 s
///     on-demand poll may consume the request inside the command's window (2026-08-20 nightly F5)
///     — so the assertion is the conjunction below instead.
/// </summary>
internal static class ApplyCommitAssertions
{
    /// <summary>
    ///     True when the observed bank changes are consistent with outbox discipline: the request
    ///     row exists (the CLI's own write), every changed table is one the verb or the relay may
    ///     touch, and any domain-table mutation carries a <c>maintenance_jobs</c> stamp (it went
    ///     through the maintenance loop, never a synchronous-only CLI write).
    /// </summary>
    public static bool IsHonestApplyCommit(
        IReadOnlyList<string> changedTables,
        string expectedOutboxTable,
        IReadOnlySet<string> targetDomainTables,
        bool requestRowExists)
    {
        if (!requestRowExists)
        {
            return false; // the outbox request is the one write the verb is allowed to make
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            expectedOutboxTable,
            "maintenance_jobs"
        };
        allowed.UnionWith(targetDomainTables);

        if (!changedTables.All(allowed.Contains))
        {
            return false; // some table the verb may not touch changed at all
        }

        if (changedTables.Any(targetDomainTables.Contains) && !changedTables.Contains("maintenance_jobs"))
        {
            return false; // a domain mutation without a maintenance-loop stamp is a synchronous CLI write
        }

        return true;
    }
}
