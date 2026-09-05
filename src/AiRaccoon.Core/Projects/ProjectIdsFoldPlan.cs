using AiRaccoon.Core.Memory;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>A loser id and the canonical winner the project-ids repair folds its rows into.</summary>
public sealed record ProjectIdFold(string Loser, string Winner);

/// <summary>
///     A map-attributed id with zero executable rows: waiting work, never a silent skip. The
///     bucket names the block kind; the reason states what the id owns and why the repair waits.
/// </summary>
public sealed record ProjectIdPin(string ProjectId, string Bucket, string Reason);

/// <summary>
///     The project-ids repair's work order, derived from a <see cref="ProjectIdCensusReport" />
///     plus the durable <see cref="ProjectIdAliasMap" /> (air-merge P2): alias losers fold into
///     their winners, drop-listed ids delete with a tombstone per removed hash, zero-entry
///     projects rows with no attachments anywhere retire, registered ids and the bank-wide
///     self-metrics sentinel are already placed (their own canonicals), and only
///     unregistered ids the map cannot attribute stay unresolved — P3 refuses their future
///     writes, but P2 neither moves nor deletes them. Map-attributed ids with zero executable
///     rows land in <see cref="Pinned" /> with a reason line (D2 planner honesty) — never a
///     silent skip that hides them from the operator.
/// </summary>
public sealed class ProjectIdsFoldPlan(
    IReadOnlyList<ProjectIdFold> folds,
    IReadOnlyList<string> dropped,
    IReadOnlyList<string> retiredProjects,
    IReadOnlyList<string> unresolved,
    IReadOnlyList<ProjectIdPin>? pinned = null)
{
    /// <summary>Shared-keyed-only ids: cross-project content the repair never folds.</summary>
    public const string PinnedSharedOnly = "pinned-shared-only";

    /// <summary>Telemetry-only ids: regenerable derived data the repair never moves.</summary>
    public const string PinnedTelemetryOnly = "pinned-telemetry-only";

    /// <summary>Ids with open workspaces: live scratch that never moves across projects.</summary>
    public const string PinnedOpenWorkspaces = "pinned-open-workspaces";

    /// <summary>Attributed ids owning no foldable surface at all: nothing to move, still reported.</summary>
    public const string PinnedNoMoveableContent = "pinned-no-moveable-content";

    public IReadOnlyList<ProjectIdFold> Folds { get; } = folds;

    public IReadOnlyList<string> Dropped { get; } = dropped;

    public IReadOnlyList<string> RetiredProjects { get; } = retiredProjects;

    public IReadOnlyList<string> Unresolved { get; } = unresolved;

    /// <summary>Attributed-but-unmovable ids, each with the reason the repair waits on it.</summary>
    public IReadOnlyList<ProjectIdPin> Pinned { get; } = pinned ?? [];

    /// <summary>True when the census left nothing to fold, delete, or retire — the repair is a no-op.</summary>
    public bool IsEmpty => Folds.Count == 0 && Dropped.Count == 0 && RetiredProjects.Count == 0;

    /// <summary>Derives the plan from a live census: every decision the map can attribute, nothing it cannot.</summary>
    public static ProjectIdsFoldPlan FromCensus(ProjectIdCensusReport report, ProjectIdAliasMap map)
    {
        Guard.IsNotNull(report);
        Guard.IsNotNull(map);
        var folds = new List<ProjectIdFold>();
        var dropped = new List<string>();
        var retired = new List<string>();
        var unresolved = new List<string>();
        var pinned = new List<ProjectIdPin>();
        foreach (var row in report.Rows)
        {
            if (map.IsDropped(row.ProjectId))
            {
                if (OwnsDeletableContent(row) || row.Registered)
                {
                    dropped.Add(row.ProjectId);
                }

                continue;
            }

            if (map.TryResolve(row.ProjectId, out var winner))
            {
                if (string.Equals(winner, row.ProjectId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (OwnsMoveableContent(row))
                {
                    folds.Add(new ProjectIdFold(row.ProjectId, winner!));
                    continue;
                }

                if (!IsRetireEligible(row))
                {
                    pinned.Add(PinUnmoveable(row.ProjectId, winner!, row));
                    continue;
                }

                // Registered-empty attributed ids fall through to the retire verdict below —
                // the retire verdict owns that shape; pinning it would hide a deletion.
            }

            // An unknown id can still carry a resolvable projects-row name — the live 01a062f4
            // guid is registered under its pre-guid name, and the map deliberately never hardcodes
            // the guid itself (P1 decision), so the name is the only attribution channel.
            if (row.RegisteredName is not null
                && map.TryResolve(row.RegisteredName, out var named)
                && !string.Equals(named, row.ProjectId, StringComparison.Ordinal))
            {
                if (OwnsMoveableContent(row))
                {
                    folds.Add(new ProjectIdFold(row.ProjectId, named!));
                    continue;
                }

                if (!IsRetireEligible(row))
                {
                    pinned.Add(PinUnmoveable(row.ProjectId, named!, row));
                    continue;
                }

                // Registered-empty ids attributed by name fall through to the retire verdict below.
            }

            if (IsRetireEligible(row))
            {
                retired.Add(row.ProjectId);
                continue;
            }

            // The bank-wide self-metrics sentinel is a real id on every deployment, not
            // machine-local attribution — the map never needs to list it to place it.
            // Explicit map entries still win: drop/alias branches above run first.
            if (string.Equals(row.ProjectId, MetricsConfigKeys.SelfMetricsProjectId, StringComparison.Ordinal))
            {
                continue;
            }

            // A registered id is already attributed by the live projects table — its own
            // canonical whether or not the map lists it, so it never needs a human.
            // The name-fold above still runs first: a registered loser the map knows
            // by its projects-row name still folds to its winner.
            if (row.Registered)
            {
                continue;
            }

            if (row.EntryTotal > 0 || row.AttachmentCount > 0)
            {
                unresolved.Add(row.ProjectId);
            }
        }

        return new ProjectIdsFoldPlan(folds, dropped, retired, unresolved, pinned);
    }

    /// <summary>
    ///     True when the row owns a surface the dropped path actually deletes: committed entries
    ///     (project/custom/shared, including null-context bulk rows — the dropped delete has no
    ///     context predicate), code, queue, discards, quality, watches, or id-embedding settings
    ///     keys. Metrics, noise, workspaces and workspace scratch are never touched, so owning only
    ///     those must not schedule a delete that re-plans forever as a no-op (review SHOULD-3).
    ///     Tombstones alone don't schedule either: no dropped step deletes them — repair-created
    ///     tombstones for dropped hashes are load-bearing suppression and must linger.
    /// </summary>
    private static bool OwnsDeletableContent(ProjectIdCensusRow row)
    {
        return row.EntryTotal > 0
            || row.CodeEntries > 0
            || row.Queued > 0
            || row.Discards > 0
            || row.QualityRows > 0
            || row.Watches > 0
            || row.WatchFiles > 0
            || row.DigestClaims > 0
            || row.SettingsKeys.Count > 0;
    }

    /// <summary>
    ///     The retire shape: registered with nothing on any surface. Checked before pinning so a
    ///     registered-empty id attributed by the map still retires — one predicate, no drift
    ///     between the attribution branches and the verdict below.
    /// </summary>
    private static bool IsRetireEligible(ProjectIdCensusRow row)
    {
        return row.Registered && row.EntryTotal == 0 && row.AttachmentCount == 0;
    }

    /// <summary>
    ///     True when the row owns a surface the repair rewrites: committed entries (project/custom
    ///     scope, any label including NULL-context bulk rows — D1, exactly the broadened applier's
    ///     executable predicate, so a planned fold can never execute as zero moves), code, queue,
    ///     discards, quality, watches, or id-embedding settings keys. Shared-scope rows are
    ///     cross-project content the repair never folds; metrics, noise, workspaces and workspace
    ///     scratch are never touched — owning only those pins with a reason (D2 planner honesty).
    /// </summary>
    private static bool OwnsMoveableContent(ProjectIdCensusRow row)
    {
        return row.ProjectEntries > 0
            || row.CustomEntries > 0
            || row.CodeEntries > 0
            || row.Queued > 0
            || row.Discards > 0
            || row.QualityRows > 0
            || row.Watches > 0
            || row.WatchFiles > 0
            || row.DigestClaims > 0
            || row.Tombstones > 0
            || row.SettingsKeys.Count > 0;
    }

    /// <summary>
    ///     Classifies an attributed-but-unmovable id into its waiting bucket with an ownership
    ///     reason. Workspaces block first (live user state), then shared-only (cross-project,
    ///     never folded), then telemetry-only (regenerable); anything left owns nothing foldable.
    /// </summary>
    private static ProjectIdPin PinUnmoveable(string loser, string winner, ProjectIdCensusRow row)
    {
        if (row.Workspaces > 0 || row.WorkspaceEntries > 0)
        {
            return new ProjectIdPin(loser, PinnedOpenWorkspaces,
                $"owns {row.Workspaces} open workspace(s) with {row.WorkspaceEntries} scratch row(s) — " +
                "workspaces never move across projects, so the id waits for its workspaces to close");
        }

        if (row.SharedEntries > 0)
        {
            return new ProjectIdPin(loser, PinnedSharedOnly,
                $"owns {row.SharedEntries} shared-scope entries (cross-project content; the repair never folds shared rows)");
        }

        if (row.MetricsRows > 0 || row.NoiseRows > 0)
        {
            return new ProjectIdPin(loser, PinnedTelemetryOnly,
                $"owns only telemetry ({row.MetricsRows} metrics + {row.NoiseRows} noise rows) — " +
                "regenerable derived data the repair never moves");
        }

        return new ProjectIdPin(loser, PinnedNoMoveableContent,
            $"is attributed to '{winner}' but owns no committed rows or other foldable surfaces — nothing to move");
    }
}
