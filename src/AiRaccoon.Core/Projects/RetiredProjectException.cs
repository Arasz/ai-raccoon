namespace AiRaccoon.Core.Projects;

/// <summary>A write named a project id the project-ids repair dropped with a tombstone (Package E,
/// D4 enforcement) — the MCP tools map this to <c>project-retired</c>. Reads never throw: visibility
/// into retired state stays available, only resurrection-by-write is refused.</summary>
public sealed class RetiredProjectException(string projectId)
    : InvalidOperationException(
        $"Project '{projectId}' is retired: the project-ids repair attributed it as dropped test residue " +
        "and deleted its rows with a tombstone. Writes under a retired id are refused — write under the " +
        "canonical project id instead.");
