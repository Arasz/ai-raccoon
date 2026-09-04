using System.CommandLine;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair project-ids` handler: a thin client over <see cref="IRepairStore" /> — the diagnose
///     report is the P1 census scanned server-side and never opens the bank from the CLI process;
///     --apply commits a request the server applies, rather than writing here. Doctor stays
///     read-only and never serves this report.
///     <para>
///         ADR-0099: the public binary ships no machine-local ids. The fold map is a one-shot
///         file parameter (<c>--map &lt;path&gt;</c>); a dry run without one plans with the empty
///         map and writes an editable template beside the bank instead of guessing folds.
///     </para>
/// </summary>
public sealed class ProjectIdsRepairCommands(IRepairStore repair)
{
    /// <summary>File name of the editable alias-map template written beside the bank on a map-less dry run.</summary>
    public const string TemplateFileName = "project-id-map.template.json";

    /// <summary>Resolves the template path for a data root.</summary>
    public static string TemplatePathFor(string dataRoot) => Path.Combine(dataRoot, TemplateFileName);

    public async Task<int> RunAsync(ParseResult parseResult, string dataRoot, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");
        var mapPath = parseResult.GetValue<string?>("--map");

        ProjectIdAliasMap map;
        string? mapJson = null;
        if (mapPath is not null)
        {
            try
            {
                // Single read: the plan parses these exact bytes and the apply forwards them,
                // so the CLI dry-run and the server job see identical content (AC3 identity).
                mapJson = File.ReadAllText(mapPath);
                map = ProjectIdAliasMap.FromJson(mapJson);
            }
            catch (Exception ex) when (ex is FileNotFoundException or JsonException or ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                await streams.WriteErrorLineAsync(
                    $"project-ids repair: cannot load --map '{mapPath}': {ex.Message}");
                return ExitCode.InvalidArgument;
            }
        }
        else
        {
            map = ProjectIdAliasMap.Empty;
        }

        var report = await repair.ReportProjectIdsAsync(cancellationToken);
        var plan = ProjectIdsFoldPlan.FromCensus(report, map);

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
            var row = report.Rows.SingleOrDefault(r => r.ProjectId == unresolved);
            var hint = row?.RegisteredName is not null ? $" (registered as '{row.RegisteredName}')" : string.Empty;
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{unresolved}' matches no known id{hint} — left alone for a human to attribute");
        }

        if (mapPath is null && !apply)
        {
            var templatePath = TemplatePathFor(dataRoot);
            var template = TryWriteTemplate(templatePath);
            await streams.WriteOutputLineAsync(
                template.Wrote
                    ? $"project-ids repair: no --map supplied — planned with the empty map (no folds). " +
                      $"Wrote an editable alias-map template to '{templatePath}'; fill in your folds and re-run with --map."
                    : template.Failure is not null
                        ? $"project-ids repair: no --map supplied — planned with the empty map (no folds). " +
                          $"Could not write the alias-map template to '{templatePath}': {template.Failure}; create it by hand or pass --map."
                        : $"project-ids repair: no --map supplied — planned with the empty map (no folds). " +
                          $"Edit the existing template at '{templatePath}' and re-run with --map.");
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
            await repair.RequestRepairAsync(RepairKind.ProjectIds, cancellationToken, mapJson);
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

    /// <summary>The outcome of attempting the template write: created, already present, or failed.</summary>
    internal sealed record TemplateWrite(bool Wrote, string? Failure);

    /// <summary>Writes the empty-map template unless the file already exists (never overwrites operator edits).</summary>
    internal static TemplateWrite TryWriteTemplate(string templatePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(templatePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            using var stream = new FileStream(templatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(ProjectIdAliasMap.Empty.ToJson(indented: true));
            return new TemplateWrite(Wrote: true, Failure: null);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070050))
        {
            // FileMode.CreateNew collides with an existing file (EEXIST on every platform) —
            // the never-overwrite rule. Any other IOException (disk full, access) is reported.
            return new TemplateWrite(Wrote: false, Failure: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TemplateWrite(Wrote: false, Failure: ex.Message);
        }
    }
}
