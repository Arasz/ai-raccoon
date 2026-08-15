namespace AiRaccoon.Core.Memory;

/// <summary>A context names a project other than the caller's — the MCP tools map this to `context-outside-project`.</summary>
public sealed class ContextOutsideProjectException(string context, string projectId)
    : InvalidOperationException(
        $"Context '{context}' is outside project '{projectId}'. An operation may only target its own project.");
