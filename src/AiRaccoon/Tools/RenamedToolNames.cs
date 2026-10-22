namespace AiRaccoon.Tools;

/// <summary>
///     The tool names the 2.0.0 rename took away, and the names that replaced them. 2.0.0 shipped
///     the rename with no aliases, so a client still speaking the old names gets nothing back
///     unless the server says which name to call instead. The map is the fact the 2.0.0 changelog
///     records (docs/changelog/2.0.0-mcp-tool-surface-renamed.md); a test compares the two.
/// </summary>
internal static class RenamedToolNames
{
    /// <summary>The release that renamed the tool surface — the version the message names.</summary>
    internal const string RenamedInVersion = "2.0.0";

    /// <summary>
    ///     Old name to current name, ordinal-compared. Every key is a name the server no longer
    ///     has, and every value a name it does — a test holds both against the live surface.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["memory_search"] = "query",
        ["memory_get"] = "get_entry",
        ["code_get"] = "get_entry",
        ["memory_list"] = "get_index",
        ["memory_stats"] = "get_store_statistics",
        ["memory_performance"] = "get_engine_statistics",
        ["memory_record_followthrough"] = "set_memory_usage_record",
        ["memory_record_grade"] = "set_query_result_grade",
        ["memory_write"] = "add_memory",
        ["memory_ingest_file"] = "ingest_file",
        ["memory_ingest_directory"] = "ingest_directory",
        ["memory_delete"] = "delete_entry",
        ["memory_delete_context"] = "delete_context",
        ["memory_share"] = "share_memory",
        ["memory_share_extract"] = "trigger_memory_sharing",
        ["memory_promotion_list"] = "get_memories_sharing_ranking",
        ["memory_promotion_discard"] = "delete_memory_from_ranking",
        ["memory_sweep"] = "trigger_memory_cleanup",
        ["memory_set_ttl"] = "set_memory_ttl",
        ["memory_workspace_begin"] = "initialize_workspace",
        ["memory_workspace_status"] = "get_workspace_status",
        ["memory_workspace_consolidate"] = "close_workspace",
        ["memory_workspace_discard"] = "discard_workspace",
        ["memory_watch_add"] = "add_watch",
        ["memory_watch_status"] = "get_watch_status",
        ["memory_watch_remove"] = "remove_watch",
        ["memory_sync"] = "sync_to_cloud",
        ["memory_embed_pending"] = "embed_deferred_entries",
        ["project_id_get"] = "get_project_id",
        ["project_id_token_get"] = "save_project_id_token"
    };
}
