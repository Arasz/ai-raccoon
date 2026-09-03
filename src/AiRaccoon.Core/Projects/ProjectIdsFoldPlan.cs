using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>A loser id and the canonical winner the project-ids repair folds its rows into.</summary>
public sealed record ProjectIdFold(string Loser, string Winner);

/// <summary>
///     The project-ids repair's work order, derived from a <see cref="ProjectIdCensusReport" />
///     plus the durable <see cref="ProjectIdAliasMap" /> (air-merge P2): alias losers fold into
///     their winners, drop-listed ids delete with a tombstone per removed hash, zero-entry
///     projects rows with no attachments anywhere retire, and ids the map cannot attribute stay
///     unresolved — P3 refuses their future writes, but P2 neither moves nor deletes them.
/// </summary>
public sealed class ProjectIdsFoldPlan(
    IReadOnlyList<ProjectIdFold> folds,
    IReadOnlyList<string> dropped,
    IReadOnlyList<string> retiredProjects,
    IReadOnlyList<string> unresolved)
{
    public IReadOnlyList<ProjectIdFold> Folds { get; } = folds;

    public IReadOnlyList<string> Dropped { get; } = dropped;

    public IReadOnlyList<string> RetiredProjects { get; } = retiredProjects;

    public IReadOnlyList<string> Unresolved { get; } = unresolved;

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
                if (!string.Equals(winner, row.ProjectId, StringComparison.Ordinal) && OwnsMoveableContent(row))
                {
                    folds.Add(new ProjectIdFold(row.ProjectId, winner!));
                }

                continue;
            }

            // An unknown id can still carry a resolvable projects-row name — the live 01a062f4
            // guid is registered under its pre-guid name, and the map deliberately never hardcodes
            // the guid itself (P1 decision), so the name is the only attribution channel.
            if (row.RegisteredName is not null
                && map.TryResolve(row.RegisteredName, out var named)
                && !string.Equals(named, row.ProjectId, StringComparison.Ordinal)
                && OwnsMoveableContent(row))
            {
                folds.Add(new ProjectIdFold(row.ProjectId, named!));
                continue;
            }

            if (row.Registered && row.EntryTotal == 0 && row.AttachmentCount == 0)
            {
                retired.Add(row.ProjectId);
                continue;
            }

            if (row.EntryTotal > 0 || row.AttachmentCount > 0)
            {
                unresolved.Add(row.ProjectId);
            }
        }

        return new ProjectIdsFoldPlan(folds, dropped, retired, unresolved);
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
    ///     True when the row owns a surface the repair rewrites. Metrics, noise, workspaces and
    ///     workspace scratch are deliberately excluded — the repair never touches them, so owning
    ///     only those must not schedule a fold.
    /// </summary>
    private static bool OwnsMoveableContent(ProjectIdCensusRow row)
    {
        return row.EntryTotal > 0
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
}
