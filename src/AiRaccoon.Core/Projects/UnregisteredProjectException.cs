namespace AiRaccoon.Core.Projects;

/// <summary>No registry row for the id, and the bank holds no rows for it either (ADR-0089 decision 3) — the MCP tools map this to `project-not-registered`.</summary>
public sealed class UnregisteredProjectException(string projectId)
    : InvalidOperationException(BuildMessage(projectId))
{
    private static string BuildMessage(string projectId)
    {
        var shown = ProjectIdText.Printable(projectId);
        var remedy = ProjectId.TryCanonicalize(projectId, out var canonical)
            ? $"or register it with 'ai-raccoon project id register {canonical}'."
            : "or fold it into a registered project with 'ai-raccoon repair project-ids --map <file> --apply'.";
        return $"Project '{shown}' is not registered. Look up this repository's id with project_id_get, " +
               $"mint a new one with project_id_token_get, {remedy}";
    }
}
