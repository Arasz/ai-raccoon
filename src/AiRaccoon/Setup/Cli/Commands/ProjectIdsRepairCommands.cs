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
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{fold.Loser}' owns {loser?.EntryTotal ?? 0} entries, " +
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
        }

        return 0;
    }
}
