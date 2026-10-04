namespace AiRaccoon.Core.Projects;

/// <summary>A call named a project id the project-ids repair dropped with a tombstone; reads and
/// writes alike are refused. The MCP tools map this to <c>project-retired</c>.</summary>
public sealed class RetiredProjectException(string projectId)
    : InvalidOperationException(
        $"Project '{projectId}' is retired: the project-ids repair attributed it as dropped test residue " +
        "and deleted its rows with a tombstone. Calls under a retired id are refused — use the " +
        "canonical project id instead.");
