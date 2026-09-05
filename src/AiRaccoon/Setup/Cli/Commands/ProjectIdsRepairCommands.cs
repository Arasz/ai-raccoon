using System.CommandLine;
using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     `repair project-ids` handler: a thin client over <see cref="IRepairStore" /> — the diagnose
///     report is the P1 census scanned server-side and never opens the bank from the CLI process;
///     --apply commits requests the server applies, rather than writing here. Doctor stays
///     read-only and never serves this report.
///     <para>
///         ADR-0099: the public binary ships no machine-local ids. The fold map is a one-shot
///         file parameter (<c>--map &lt;path&gt;</c>); a dry run without one plans with the empty
///         map and writes an editable template beside the bank instead of guessing folds.
///     </para>
///     <para>
///         Package F (D5): plain --apply runs the loop — derive→commit-request→poll→reap→re-derive
///         until converged, pinned-only, stuck, or writers-active, bounded by
///         <see cref="RepairLoopOptions" />. --queue-only preserves the old fire-and-forget for
///         scripts. Every pass re-derives first, so a pinned-only plan reports pinned-only without
///         committing a blind repair_requests row (review #614); stuck-vs-writers-active is measured
///         from per-pass moved-counts plus census totals, never guessed.
///     </para>
/// </summary>
public sealed class ProjectIdsRepairCommands
{
    /// <summary>File name of the editable alias-map template written beside the bank on a map-less dry run.</summary>
    public const string TemplateFileName = "project-id-map.template.json";

    /// <summary>
    ///     P3 status note on the converged/pinned-only verdicts. The durable alias map plus the
    ///     refuse/fold-through write gate land in packages D+E — until then the loop reports the
    ///     D6 counts honestly and names what is still pending, instead of claiming "P3 armed".
    ///     Package G flips this note once the end-to-end proof exists.
    /// </summary>
    public const string P3PendingNote = "P3 pending (durable map + write-gate land in packages D+E)";

    private readonly IRepairStore _repair;

    private readonly RepairLoopOptions _options;

    private readonly TimeProvider _timeProvider;

    public ProjectIdsRepairCommands(IRepairStore repair, RepairLoopOptions? options = null, TimeProvider? timeProvider = null)
    {
        Guard.IsNotNull(repair);
        _repair = repair;
        _options = options ?? RepairLoopOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    ///     Bounds for the run-until-fixed loop. One server maintenance poll is ~15s (H7), so each
    ///     pass waits one poll; ≤10 passes cap the committed repair_requests rows at ten and the
    ///     wall clock near three minutes, with a 10-minute total backstop for loaded machines.
    /// </summary>
    public sealed record RepairLoopOptions(int MaxPasses, TimeSpan PollInterval, TimeSpan TotalBudget)
    {
        public static RepairLoopOptions Default { get; } = new(10, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(10));

        internal static RepairLoopOptions Test { get; } = new(3, TimeSpan.Zero, TimeSpan.FromMinutes(10));
    }

    /// <summary>Resolves the template path for a data root.</summary>
    public static string TemplatePathFor(string dataRoot) => Path.Combine(dataRoot, TemplateFileName);

    public async Task<int> RunAsync(ParseResult parseResult, string dataRoot, StandardStreams streams, CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");
        var queueOnly = parseResult.GetValue<bool>("--queue-only");
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

        var report = await _repair.ReportProjectIdsAsync(cancellationToken);
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
            if (queueOnly)
            {
                var queued = ActionableCount(plan);
                if (queued > 0)
                {
                    await RequestOnceAsync(plan, mapJson, streams, cancellationToken);
                    await streams.WriteOutputLineAsync(
                        $"project-ids repair: summary — repair in progress: {queued} change(s) queued for the server — " +
                        "the server applies it on its next maintenance poll (~15s).");
                }
                else
                {
                    await WriteSettledSummaryAsync(plan, streams);
                }

                return 0;
            }

            await RunRepairLoopAsync(map, mapJson, report, plan, streams, cancellationToken);
            return 0;
        }

        await WriteSettledSummaryAsync(plan, streams);
        return 0;
    }

    /// <summary>
    ///     Fire-and-forget for scripts: commit one request when the first derive is actionable,
    ///     then exit without polling — and commit nothing on a pinned-only plan (review #614).
    /// </summary>
    private async Task RequestOnceAsync(ProjectIdsFoldPlan plan, string? mapJson, StandardStreams streams, CancellationToken cancellationToken)
    {
        var actionable = ActionableCount(plan);
        if (actionable == 0)
        {
            return;
        }

        await _repair.RequestRepairAsync(RepairKind.ProjectIds, cancellationToken, mapJson);
        await streams.WriteOutputLineAsync(
            "project-ids repair: request committed; the server applies it and drains the resulting embeddings " +
            "on its next maintenance poll (~15s) — nothing left to run by hand.");
        // d-426 SHOULD-5: the fold is single-pass — a concurrent loser write re-creates the
        // loser key behind the plan's back. The receipt states the quiesce-or-rerun rule.
        await streams.WriteOutputLineAsync(
            "project-ids repair: the fold is single-pass — quiesce writers under a folded id, or re-run " +
            "'repair project-ids' until it reports no folds.");
    }

    /// <summary>
    ///     The run-until-fixed loop (D5): each pass re-derives first, commits one request while the
    ///     plan is actionable, polls one maintenance interval, and reaps the next derive. Stops are
    ///     converged | pinned-only | attention (zero actionable — nothing is ever committed for
    ///     those) | stuck (identical actionable set across 2 passes with zero rows moved and no
    ///     total growth) | writers-active (census totals grew — quiesce, loop to bound, then report).
    /// </summary>
    private async Task RunRepairLoopAsync(ProjectIdAliasMap map, string? mapJson, ProjectIdCensusReport report, ProjectIdsFoldPlan plan, StandardStreams streams, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var firstTotal = CensusTotal(report);
        var latestTotal = firstTotal;
        var pass = 0;
        string? previousSignature = null;
        var previousMoved = 0L;
        var previousGrew = false;
        while (true)
        {
            var actionable = ActionableCount(plan);
            if (actionable == 0)
            {
                await WriteSettledSummaryAsync(plan, streams);
                return;
            }

            var signature = ActionableSignature(plan);
            if (pass > 0 && string.Equals(signature, previousSignature, StringComparison.Ordinal) && previousMoved == 0 && !previousGrew)
            {
                await streams.WriteOutputLineAsync(
                    $"project-ids repair: stuck — identical actionable set across 2 passes with zero rows moved " +
                    $"(actionable: {signature}); quiesce writers under folded ids and check the server log for " +
                    "the job receipt, then re-run 'repair project-ids'.");
                await streams.WriteOutputLineAsync(
                    $"project-ids repair: summary — stuck: {CountsLine(plan)} — identical actionable set across " +
                    "2 passes with zero rows moved; quiesce writers under folded ids, then re-run.");
                return;
            }

            if (pass >= _options.MaxPasses || _timeProvider.GetElapsedTime(started) >= _options.TotalBudget)
            {
                if (latestTotal > firstTotal)
                {
                    var stuckIds = string.Join(", ", plan.Folds.Select(fold => $"'{fold.Loser}'"));
                    await streams.WriteOutputLineAsync(
                        $"project-ids repair: summary — writers-active: {CountsLine(plan)} — census totals grew " +
                        $"{firstTotal} → {latestTotal} entries across {pass} pass(es); quiesce writers under " +
                        $"folded ids ({stuckIds}), then re-run 'repair project-ids'.");
                }
                else
                {
                    await streams.WriteOutputLineAsync(
                        $"project-ids repair: summary — stuck: {CountsLine(plan)} — still actionable after " +
                        $"{pass} pass(es) with no census growth; quiesce writers under folded ids and check " +
                        "the server log for the job receipt, then re-run.");
                }

                return;
            }

            var beforeTotal = CensusTotal(report);
            var beforeActionableEntries = ActionableEntries(report, plan);
            await _repair.RequestRepairAsync(RepairKind.ProjectIds, cancellationToken, mapJson);
            await streams.WriteOutputLineAsync(
                $"project-ids repair: pass {pass + 1}/{_options.MaxPasses} — derived {plan.Folds.Count} fold, " +
                $"{plan.Dropped.Count} drop, {plan.RetiredProjects.Count} retire; request committed; the server " +
                "applies it on its next maintenance poll (~15s).");

            await Task.Delay(_options.PollInterval, _timeProvider, cancellationToken);
            var after = await _repair.ReportProjectIdsAsync(cancellationToken);
            var afterPlan = ProjectIdsFoldPlan.FromCensus(after, map);
            var moved = Math.Max(0, beforeActionableEntries - ActionableEntries(after, plan));
            var afterTotal = CensusTotal(after);
            await streams.WriteOutputLineAsync(
                $"project-ids repair: pass {pass + 1}/{_options.MaxPasses} — reaped: moved {moved} row(s); " +
                $"census totals {beforeTotal} → {afterTotal} entries.");
            if (afterTotal > beforeTotal)
            {
                await streams.WriteOutputLineAsync(
                    $"project-ids repair: pass {pass + 1}/{_options.MaxPasses} — census totals grew " +
                    $"({beforeTotal} → {afterTotal}); writers are active under folded ids — quiesce writers, " +
                    "then the loop re-checks.");
            }

            previousSignature = signature;
            previousMoved = moved;
            previousGrew = afterTotal > beforeTotal;
            latestTotal = afterTotal;
            report = after;
            plan = afterPlan;
            pass++;
        }
    }

    /// <summary>
    ///     The closing verdict for a settled plan — dry runs, queue-only exits, and loop ends share
    ///     one D6-vocabulary grammar, always the last line: converged | pinned-only | repair needed |
    ///     attention needed, each with explicit zero-inclusive counts (F2 supersedes the 1.40.2 strings).
    /// </summary>
    private static async Task WriteSettledSummaryAsync(ProjectIdsFoldPlan plan, StandardStreams streams)
    {
        var actionable = ActionableCount(plan);
        if (actionable > 0)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: summary — repair needed: {CountsLine(plan)} — pass --apply to run " +
                (plan.Unresolved.Count > 0
                    ? $"the loop until it reports converged; {plan.Unresolved.Count} id(s) still need a human to attribute."
                    : "the loop until it reports converged."));
            return;
        }

        if (plan.Unresolved.Count > 0)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: summary — attention needed: {CountsLine(plan)} — " +
                $"{plan.Unresolved.Count} id(s) still need a human to attribute.");
            return;
        }

        if (plan.Pinned.Count > 0)
        {
            await streams.WriteOutputLineAsync(
                $"project-ids repair: summary — pinned-only: {CountsLine(plan)}, {P3PendingNote}.");
            return;
        }

        await streams.WriteOutputLineAsync(
            $"project-ids repair: summary — converged: {CountsLine(plan)}, {P3PendingNote}.");
    }

    /// <summary>D6 counts fragment shared by every closing line: explicit zeros, inline pin list.</summary>
    private static string CountsLine(ProjectIdsFoldPlan plan) =>
        $"{plan.Folds.Count} fold, {plan.Dropped.Count} drop, {plan.RetiredProjects.Count} retire, " +
        $"{plan.Unresolved.Count} unresolved, {plan.Pinned.Count} pinned{PinsInline(plan)}";

    /// <summary>Inline pin list for the counts fragment; empty when nothing waits.</summary>
    private static string PinsInline(ProjectIdsFoldPlan plan) =>
        plan.Pinned.Count == 0
            ? string.Empty
            : $" ({string.Join(", ", plan.Pinned.Select(pin => $"{pin.Bucket}: '{pin.ProjectId}'"))})";

    /// <summary>Changes the server can still apply: folds plus drops plus retires.</summary>
    private static int ActionableCount(ProjectIdsFoldPlan plan) =>
        plan.Folds.Count + plan.Dropped.Count + plan.RetiredProjects.Count;

    /// <summary>
    ///     The loop's stuck comparator: the ordered actionable set (fold losers with winners, drops,
    ///     retires). Order-normalised so plan enumeration order never fakes progress or stuckness.
    /// </summary>
    private static string ActionableSignature(ProjectIdsFoldPlan plan) =>
        string.Join(";", plan.Folds
            .Select(fold => $"F:{fold.Loser}->{fold.Winner}")
            .Concat(plan.Dropped.Select(dropped => $"D:{dropped}"))
            .Concat(plan.RetiredProjects.Select(retired => $"R:{retired}"))
            .OrderBy(part => part, StringComparer.Ordinal));

    /// <summary>Committed entries owned by the plan's actionable ids — the moved-rows instrument.</summary>
    private static long ActionableEntries(ProjectIdCensusReport report, ProjectIdsFoldPlan plan)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fold in plan.Folds)
        {
            ids.Add(fold.Loser);
        }

        foreach (var dropped in plan.Dropped)
        {
            ids.Add(dropped);
        }

        foreach (var retired in plan.RetiredProjects)
        {
            ids.Add(retired);
        }

        return report.Rows.Where(row => ids.Contains(row.ProjectId)).Sum(row => row.EntryTotal);
    }

    /// <summary>Bank-wide committed entries — the writers-active instrument.</summary>
    private static long CensusTotal(ProjectIdCensusReport report) =>
        report.Rows.Sum(row => row.EntryTotal);

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
