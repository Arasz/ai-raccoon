namespace AiRaccoon.Observability;

/// <summary>Maps MCP tool names to the single status word shown while the call runs.</summary>
public static class StatusWord
{
    private static readonly Dictionary<string, string> Words = new()
    {
        ["memory_write"] = "remembering",
        ["memory_search"] = "searching",
        ["memory_list"] = "listing",
        ["memory_stats"] = "counting",
        ["memory_share"] = "sharing",
        ["memory_share_extract"] = "extracting",
        ["memory_delete"] = "forgetting",
        ["memory_delete_context"] = "forgetting",
        ["memory_ingest_file"] = "ingesting",
        ["memory_ingest_directory"] = "ingesting",
        ["memory_embed_pending"] = "embedding",
        ["memory_workspace_begin"] = "opening",
        ["memory_workspace_status"] = "checking",
        ["memory_workspace_consolidate"] = "consolidating",
        ["memory_workspace_discard"] = "discarding",
        ["memory_sweep"] = "sweeping",
        ["memory_sync"] = "syncing",
        ["memory_watch_add"] = "watching",
        ["memory_watch_status"] = "watching",
        ["memory_watch_remove"] = "watching"
    };

    /// <summary>One word for the tool; unknown tools fall back to the name minus the memory_ prefix.</summary>
    public static string For(string tool)
    {
        if (Words.TryGetValue(tool, out var word))
        {
            return word;
        }

        if (tool.StartsWith("memory_", StringComparison.Ordinal))
        {
            var suffix = tool["memory_".Length..];
            return string.IsNullOrEmpty(suffix) ? "working" : suffix;
        }

        return tool == "memory" ? "working" : tool;
    }
}
