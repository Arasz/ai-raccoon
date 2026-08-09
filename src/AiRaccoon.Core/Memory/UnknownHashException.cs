namespace AiRaccoon.Core.Memory;

/// <summary>No entry with the given content hash exists in the project's scope — the MCP tools map this to `unknown-hash`.</summary>
public sealed class UnknownHashException(string hash, string projectId)
    : InvalidOperationException($"No entry with hash '{hash}' in project '{projectId}'.");
