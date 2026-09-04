using System.CommandLine;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair project-ids` handler: a thin client over <see cref="IRepairStore" /> — the diagnose
///     report is the P1 census scanned server-side and never opens the bank from the CLI process;
///     --apply commits a request the server applies, rather than writing here. Doctor stays
///     read-only and never serves this report.
/// </summary>
public sealed class ProjectIdsRepairCommands(IRepairStore repair)
{
    public async Task<int> RunAsync(ParseResult parseResult, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");

        var report = await repair.ReportProjectIdsAsync(cancellationToken);
        var plan = ProjectIdsFoldPlan.FromCensus(report, ProjectIdAliasMap.Default);

        // Every censused id lands in exactly one bucket: FromCensus's `continue`s are exhaustive
        // (fold, drop, retire) or the id needs nothing further (already canonical under the map,
        // or genuinely empty and unregistered) — `needsAttention` is the one bucket the operator
        // must act on; the rest either changes on its own or was never a problem.
        var needsAttention = plan.Unresolved.Count;
        var needsNothing = report.Rows.Count - plan.Folds.Count - plan.Dropped.Count -
            plan.RetiredProjects.Count - needsAttention;
        var verb = apply ? "Request will be queued for the server." : "Dry run — pass --apply to run this.";
        await streams.WriteOutputLineAsync(
            $"project-ids repair: {report.Rows.Count} id(s) censused — {plan.Folds.Count} fold, " +
            $"{plan.Dropped.Count} drop (test residue), {plan.RetiredProjects.Count} retire (registered, empty), " +
            $"{needsAttention} need a human to attribute, {needsNothing} need nothing (already correct or empty). {verb}");

        var orphans = report.Rows.Count(row => row.Orphan);
        if (orphans > 0)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: {orphans} id(s) own entries with no projects-table registration.");
        }

        foreach (var fold in plan.Folds)
        {
            var loser = report.Rows.SingleOrDefault(row => row.ProjectId == fold.Loser);
            // d-426 SHOULD-2: expose each loser's NULL-context count at the pre-apply surface.
            // The repair's keep predicate (project-scope + non-NULL label) deliberately leaves
            // those rows behind — decided: keep predicate, expose counts, P-INT asserts — so the
            // count here is the operator's verify-zero-or-broaden instrument before --apply: a
            // nonzero count stays loser-keyed by design, never a surprise orphan afterwards.
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{fold.Loser}' owns {loser?.EntryTotal ?? 0} entries " +
                $"({loser?.NullContextEntries ?? 0} NULL-context, stay), " +
                $"{loser?.Queued ?? 0} queued — folds to '{fold.Winner}'");
        }

        foreach (var retired in plan.RetiredProjects)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{retired}' is registered with nothing left on it — retires (registry row removed)");
        }

        foreach (var dropped in plan.Dropped)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{dropped}' is test residue — deletes with a tombstone per removed hash");
        }

        foreach (var unresolved in plan.Unresolved)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{unresolved}' matches no known id — left alone for a human to attribute");
        }

        if (needsAttention > 0)
        {
            // The only other re-run guidance (below, apply-only) is about a concurrent-write
            // hazard racing a single apply pass — a completely different reason to re-run than
            // "the map still can't place this id." Without this line, an operator watching the
            // same needsAttention count across repeated runs has no way to tell those two apart.
            await streams.WriteOutputLineAsync(
                $"project-ids repair: re-running will not clear the {needsAttention} id(s) above that need a " +
                "human — attribute them, or wait for an alias-map update.");
        }

        if (apply)
        {
            await repair.RequestRepairAsync(RepairKind.ProjectIds, cancellationToken);
            await streams.WriteOutputLineAsync(
                "project-ids repair: request committed; the server applies it and drains the resulting embeddings " +
                "on its next maintenance poll (~15s) — nothing left to run by hand.");
            // d-426 SHOULD-5: the fold is single-pass — a concurrent loser write re-creates the
            // loser key behind the plan's back. The receipt states the quiesce-or-rerun rule.
            await streams.WriteOutputLineAsync(
                "project-ids repair: the fold is single-pass — quiesce writers under a folded id, or re-run " +
                "'repair project-ids' until it reports no folds.");
        }

        return 0;
    }
}
