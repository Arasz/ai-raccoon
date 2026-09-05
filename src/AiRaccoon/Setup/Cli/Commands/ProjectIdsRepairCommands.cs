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
        // (fold, drop, retire, pin) or the id needs nothing further (already canonical under the map,
        // or genuinely empty and unregistered) — `needsAttention` is the one bucket the operator
        // must act on; pins wait with reasons below; the rest either changes on its own or was never
        // a problem.
        var needsAttention = plan.Unresolved.Count;
        var needsNothing = report.Rows.Count - plan.Folds.Count - plan.Dropped.Count -
            plan.RetiredProjects.Count - needsAttention - plan.Pinned.Count;
        var verb = apply ? "Request will be queued for the server." : "Dry run — pass --apply to run this.";
        var pinnedSegment = plan.Pinned.Count > 0 ? $", {plan.Pinned.Count} pinned (waiting with reasons below)" : string.Empty;
        await streams.WriteOutputLineAsync(
            $"project-ids repair: {report.Rows.Count} id(s) censused — {plan.Folds.Count} fold, " +
            $"{plan.Dropped.Count} drop (test residue), {plan.RetiredProjects.Count} retire (registered, empty), " +
            $"{needsAttention} need a human to attribute{pinnedSegment}, {needsNothing} need nothing (already correct or empty). {verb}");

        var orphans = report.Rows.Count(row => row.Orphan);
        if (orphans > 0)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: {orphans} id(s) own entries with no projects-table registration.");
        }

        foreach (var fold in plan.Folds)
        {
            var loser = report.Rows.SingleOrDefault(row => row.ProjectId == fold.Loser);
            // D1 overturns the d-426 keep: NULL-context rows are committed rows and fold to the
            // winner with every other committed row. The count stays visible pre-apply as the
            // verify-they-move instrument: a nonzero count folds by design, never a surprise orphan.
            await streams.WriteOutputLineAsync(
                $"project-ids repair: '{fold.Loser}' owns {loser?.EntryTotal ?? 0} entries " +
                $"({loser?.NullContextEntries ?? 0} NULL-context, fold), " +
                $"{loser?.Queued ?? 0} queued — folds to '{fold.Winner}'");
        }

        if (plan.Pinned.Count > 0)
        {
            // One id per line, like the unresolved bucket: the scoreboard above carries the count.
            await streams.WriteOutputLineAsync(
                $"project-ids repair: {plan.Pinned.Count} id(s) pinned — waiting with reasons below:");
            foreach (var pin in plan.Pinned)
            {
                await streams.WriteOutputLineAsync($"  {pin.Bucket}: '{pin.ProjectId}' — {pin.Reason}");
            }
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

        if (plan.Unresolved.Count > 0)
        {
            // Header plus one id per line: a 40-id comma-joined line wraps unreadably.
            // The scoreboard above already carries the count; registered-name hints stay inline per id.
            await streams.WriteOutputLineAsync(
                $"project-ids repair: {plan.Unresolved.Count} id(s) match no known id — left alone for a human to attribute:");
            foreach (var id in plan.Unresolved)
            {
                var row = report.Rows.SingleOrDefault(r => r.ProjectId == id);
                var hint = row?.RegisteredName is not null ? $" (registered as '{row.RegisteredName}')" : string.Empty;
                await streams.WriteOutputLineAsync($"  '{id}'{hint}");
            }
        }

        if (mapPath is null && !apply)
        {
            var templatePath = TemplatePathFor(dataRoot);
            // Template-only runs always plan with the empty map, so no fold/drop can exist here:
            // registered non-retired ids are exactly the canonical-branch rows worth seeding.
            var canonicalSeed = report.Rows
                .Where(row => row.Registered && !plan.RetiredProjects.Contains(row.ProjectId, StringComparer.Ordinal))
                .Select(row => row.ProjectId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var template = TryWriteTemplate(templatePath, plan.Unresolved, canonicalSeed);
            await streams.WriteOutputLineAsync(
                template.Wrote
                    ? $"project-ids repair: no --map supplied — planned with the empty map (no folds). " +
                      $"Wrote an editable alias-map template to '{templatePath}' (example alias shape, " +
                      $"__self_metrics__ + {canonicalSeed.Count} registered canonical(s), {plan.Unresolved.Count} unattributed id(s) " +
                      $"pre-filled in Dropped for review); edit Aliases/Dropped and re-run with --map."
                    : template.Failure is not null
                        ? $"project-ids repair: no --map supplied — planned with the empty map (no folds). " +
                          $"Could not write the alias-map template to '{templatePath}': {template.Failure.TrimEnd('.', ' ')}; create it by hand or pass --map."
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

        // f: the report never closed with a verdict — one summary line, always last, naming
        // the overall state: repair in progress (--apply queued it), repair needed (a dry run
        // with folds/drops/retires waiting), pinned-only (a dry run with nothing actionable but
        // waiting pins — D6 vocabulary), or nothing to do (converged). A pending
        // human-attribution count qualifies the line but never multiplies the states; a pinned
        // count extends it the same way. All three 1.40.2 lines stay byte-identical when no pins exist.
        var actionable = plan.Folds.Count + plan.Dropped.Count + plan.RetiredProjects.Count;
        var pinnedQualifier = plan.Pinned.Count > 0 ? $"; {plan.Pinned.Count} pinned with reasons above" : string.Empty;
        if (apply)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: summary — repair in progress ({actionable} change(s) queued for the server{pinnedQualifier}).");
        }
        else if (actionable > 0)
        {
            await streams.WriteOutputLineAsync(
                needsAttention > 0
                    ? $"project-ids repair: summary — repair needed ({actionable} change(s) waiting on --apply; {needsAttention} id(s) still need a human{pinnedQualifier})."
                    : $"project-ids repair: summary — repair needed ({actionable} change(s) waiting on --apply{pinnedQualifier}).");
        }
        else
        {
            await streams.WriteOutputLineAsync(
                needsAttention > 0
                    ? $"project-ids repair: summary — nothing to queue ({needsAttention} id(s) still need a human to attribute{pinnedQualifier})."
                    : plan.Pinned.Count > 0
                        ? $"project-ids repair: summary — pinned-only (0 change(s) waiting on --apply{pinnedQualifier})."
                        : "project-ids repair: summary — nothing to do (no folds, drops, or retires pending).");
        }

        return 0;
    }

    /// <summary>The outcome of attempting the template write: created, already present, or failed.</summary>
    internal sealed record TemplateWrite(bool Wrote, string? Failure);

    /// <summary>
    ///     Writes the seed template unless the file already exists (never overwrites operator edits).
    ///     The seed is an editing starting point, not a verdict: one example alias shape, the bank-wide
    ///     self-metrics canonical (a real id on every deployment), the census's registered ids as
    ///     further canonicals, and the current unresolved ids pre-filled into Dropped for the
    ///     operator to review — move true folds to Aliases, keep true residue in Dropped,
    ///     never --apply the seed blindly.
    /// </summary>
    internal static TemplateWrite TryWriteTemplate(string templatePath, IReadOnlyList<string> droppedSeed, IReadOnlyList<string> canonicalSeed)
    {
        try
        {
            var dir = Path.GetDirectoryName(templatePath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var seed = new ProjectIdAliasMap(
                [new ProjectIdAliasEntry("old-project-id", "new-project-id")],
                [MetricsConfigKeys.SelfMetricsProjectId, .. canonicalSeed],
                droppedSeed);
            using var stream = new FileStream(templatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(seed.ToJson(indented: true));
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
            // Portable never-overwrite rule: the EEXIST HResult above does not fire on every
            // platform (observed live: the collision surfaces here with "already exists" text),
            // so an existing file means operator edits, whatever the IOException says.
            if (File.Exists(templatePath))
            {
                return new TemplateWrite(Wrote: false, Failure: null);
            }

            return new TemplateWrite(Wrote: false, Failure: ex.Message);
        }
    }
}
