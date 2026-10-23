namespace AiRaccoon.Core.Memory;

/// <summary>The hash names an entry of the caller's project that is not a Memory entry, so it cannot take a TTL — the MCP tools map this to `not-a-memory-entry`.</summary>
public sealed class NotAMemoryEntryException(string hash, string projectId)
    : InvalidOperationException($"Entry '{hash}' in project '{projectId}' is not a memory entry; only memory entries take a TTL. Pass ttlDays null to clear a TTL it already carries.");
