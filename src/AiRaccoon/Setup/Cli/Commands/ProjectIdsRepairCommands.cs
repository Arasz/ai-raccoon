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

        var verb = apply ? "queued for the server to fold" : "would fold (dry run; pass --apply to queue it)";
        var orphans = report.Rows.Count(row => row.Orphan);
        await streams.WriteOutputLineAsync(
            $"project-ids repair: {report.Rows.Count} id(s) hold rows, {orphans} orphan(s), " +
            $"{report.ZeroEntryRows.Count} zero-entry row(s) {verb}");
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
