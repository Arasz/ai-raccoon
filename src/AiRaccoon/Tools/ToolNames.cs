namespace AiRaccoon.Tools;

/// <summary>The MCP tool names, one constant per tool. A test keeps this list equal to the generated tool inventory.</summary>
internal static class ToolNames
{
    public const string AddMemory = "add_memory";
    public const string AddWatch = "add_watch";
    public const string AmendDecision = "amend_decision";
    public const string CloseWorkspace = "close_workspace";
    public const string DeleteContext = "delete_context";
    public const string DeleteEntry = "delete_entry";
    public const string DeleteMemoryFromRanking = "delete_memory_from_ranking";
    public const string DiscardWorkspace = "discard_workspace";
    public const string EmbedDeferredEntries = "embed_deferred_entries";
    public const string ExportDecisions = "export_decisions";
    public const string FindDecisionPrecedents = "find_decision_precedents";
    public const string GetDecision = "get_decision";
    public const string GetDecisionGraph = "get_decision_graph";
    public const string GetEngineStatistics = "get_engine_statistics";
    public const string GetEntry = "get_entry";
    public const string GetIndex = "get_index";
    public const string GetMemoriesSharingRanking = "get_memories_sharing_ranking";
    public const string GetProjectId = "get_project_id";
    public const string GetQualityReport = "get_quality_report";
    public const string GetStoreStatistics = "get_store_statistics";
    public const string GetWatchStatus = "get_watch_status";
    public const string GetWorkspaceStatus = "get_workspace_status";
    public const string IngestDirectory = "ingest_directory";
    public const string IngestFile = "ingest_file";
    public const string InitializeWorkspace = "initialize_workspace";
    public const string LinkDecisions = "link_decisions";
    public const string PreviewDecisions = "preview_decisions";
    public const string Query = "query";
    public const string QueryDecisions = "query_decisions";
    public const string RecordDecision = "record_decision";
    public const string RegisterDecisionTask = "register_decision_task";
    public const string RemoveWatch = "remove_watch";
    public const string SaveProjectIdToken = "save_project_id_token";
    public const string SetMemoryTtl = "set_memory_ttl";
    public const string SetMemoryUsageRecord = "set_memory_usage_record";
    public const string SetQueryResultGrade = "set_query_result_grade";
    public const string ShareMemory = "share_memory";
    public const string SupersedeDecision = "supersede_decision";
    public const string SyncToCloud = "sync_to_cloud";
    public const string TransitionDecision = "transition_decision";
    public const string TriggerMemoryCleanup = "trigger_memory_cleanup";
    public const string TriggerMemorySharing = "trigger_memory_sharing";
    public const string UnlinkDecisions = "unlink_decisions";
}
