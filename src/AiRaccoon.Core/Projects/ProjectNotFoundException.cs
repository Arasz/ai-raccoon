namespace AiRaccoon.Core.Projects;

/// <summary>No registered project has this exact name; the MCP tools map this to <c>project-not-found</c>.</summary>
public sealed class ProjectNotFoundException(string name)
    : InvalidOperationException(
        $"No project is registered under the name '{ProjectIdText.Printable(name)}'. Call project_id_token_get with this name to mint and register one.");
