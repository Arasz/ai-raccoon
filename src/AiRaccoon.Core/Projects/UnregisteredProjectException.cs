namespace AiRaccoon.Core.Projects;

/// <summary>No registry row for the id, and the bank holds no rows for it either (ADR-0089 decision 3) — the MCP tools map this to `project-not-registered`.</summary>
public sealed class UnregisteredProjectException(string projectId)
    : InvalidOperationException(
        $"Project '{projectId}' is not registered. Look up this repository's id with project_id_get, " +
        "mint a new one with project_id_token_get, or register an id you already use with " +
        $"'ai-raccoon project id register {projectId}'.");
