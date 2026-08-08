namespace AiRaccoon.Core.Workspace;

/// <summary>The workspace does not exist or is no longer active — the MCP tools map this to `unknown-workspace`.</summary>
public sealed class UnknownWorkspaceException(string workspaceId, string projectId)
    : InvalidOperationException($"Workspace '{workspaceId}' does not exist for project '{projectId}'.");
