namespace AiRaccoon.Core.Projects;

/// <summary>No registry row for the id, and the bank holds no rows for it either (ADR-0089 decision 3) — the MCP tools map this to `project-not-registered`.</summary>
public sealed class UnregisteredProjectException(string projectId)
    : InvalidOperationException(
        $"Project '{projectId}' is not registered. Call project_id_token_get to mint and register a project id before writing.");
