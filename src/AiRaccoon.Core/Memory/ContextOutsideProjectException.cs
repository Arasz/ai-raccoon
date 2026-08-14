namespace AiRaccoon.Core.Memory;

/// <summary>A context names a project other than the caller's — the MCP tools map this to `context-outside-project`.</summary>
public sealed class ContextOutsideProjectException(string context, string projectId)
    : InvalidOperationException(
        $"Context '{context}' writes into a project other than '{projectId}'. A write may only target its own project.");
