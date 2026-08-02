namespace AiRaccon.Infrastructure.Sqlite;

/// <summary>SQL for the sqlite-memory surface; kept in one place so the store stays thin (spec §6.1).</summary>
internal static class MemorySql
{
    public const string InsertText = "SELECT memory_add_text(@content, @context)";

    public const string SelectEntryByHash = "SELECT hash, path, context, value, created_at FROM dbmem_content WHERE hash = @hash";

    public const string SelectSourceByHashAndContext = "SELECT path, value, created_at FROM dbmem_content WHERE hash = @hash AND context = @context LIMIT 1";

    public const string SearchWithContext = """
        SELECT hash, seq, ranking, path, snippet
        FROM memory_search
        WHERE query = @query
          AND context = @context
          AND ranking >= @minScore
        ORDER BY ranking DESC
        LIMIT @limit
        """;

    public const string Delete = "SELECT memory_delete(@hash)";

    public const string DeleteContext = "SELECT memory_delete_context(@context)";

    public const string CountEntries = "SELECT count(*) FROM dbmem_content";

    public const string PendingCount = "SELECT memory_pending_count()";

    /// <summary>The bank's committed contexts — shared plus every distinct project context (FR-MEM-1.16); workspaces excluded, shared first.</summary>
    public const string CommittedContexts = """
        SELECT DISTINCT context
        FROM dbmem_content
        WHERE context = 'shared' OR context LIKE 'project:%'
        ORDER BY CASE WHEN context = 'shared' THEN 0 ELSE 1 END, context
        """;
}
