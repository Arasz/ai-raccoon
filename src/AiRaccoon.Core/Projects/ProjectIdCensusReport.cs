using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>One id's share of every id-keyed bank surface (air-merge P1 census).</summary>
public sealed record ProjectIdCensusRow(
    string ProjectId,
    bool Registered,
    string? RegisteredName,
    long ProjectEntries,
    long CustomEntries,
    long SharedEntries,
    long WorkspaceEntries,
    long NullContextEntries,
    long CodeEntries,
    long CodeFtsRows,
    long VecCodeRows,
    long EntriesFtsRows,
    long VecEntryRows,
    long VecStructureRows,
    long Queued,
    long Discards,
    long QualityRows,
    long Watches,
    long WatchFiles,
    long DigestClaims,
    long Tombstones,
    long Workspaces,
    long MetricsRows,
    long NoiseRows,
    IReadOnlyList<string> SettingsKeys)
{
    /// <summary>Committed entries rows (project + custom + shared scope).</summary>
    public long EntryTotal => ProjectEntries + CustomEntries + SharedEntries;

    /// <summary>Owns entries but has no projects row — the research record's orphan shape.</summary>
    public bool Orphan => !Registered && EntryTotal > 0;

    /// <summary>Non-entry attachments that must be reviewed before any retire/delete decision. Telemetry (metrics/noise) is excluded: regenerable derived data, never verdict-blocking (D3).</summary>
    public long AttachmentCount =>
        Queued + Discards + QualityRows + Watches + WatchFiles + DigestClaims +
        Tombstones + Workspaces + WorkspaceEntries + CodeEntries + SettingsKeys.Count;
}

/// <summary>Bank-wide census report: one row per id found on any id-keyed surface, plus NULL counters.</summary>
public sealed record ProjectIdCensusReport(
    IReadOnlyList<ProjectIdCensusRow> Rows,
    long NullScopeEntries,
    long NullContextEntries,
    long NullProjectEntries,
    long NullQualityRows,
    IReadOnlyList<string> UnattributedSettingsKeys)
{
    /// <summary>Rows owning no entries — retire/delete candidates with their attachments listed on the row.</summary>
    public IReadOnlyList<ProjectIdCensusRow> ZeroEntryRows => Rows.Where(r => r.EntryTotal == 0).ToList();

    /// <summary>Throws when the id owns nothing on any surface.</summary>
    public ProjectIdCensusRow Row(string projectId)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        return Rows.Single(r => r.ProjectId == projectId);
    }
}
