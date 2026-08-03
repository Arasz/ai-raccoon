namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>SQL for the sqlite-memory surface; kept in one place so the store stays thin (spec §6.1).</summary>
internal static class MemorySql
{
    public const string InsertText = "SELECT memory_add_text(@content, @context)";

    public const string SelectEntryByHash = "SELECT hash, path, context, value, created_at FROM dbmem_content WHERE hash = @hash";

    public const string SelectEntryByContextAndValue = """
                                                       SELECT hash AS Hash, path AS Path, context AS Context, value AS Value, created_at AS CreatedAt
                                                       FROM dbmem_content
                                                       WHERE context = @context AND value = @content
                                                       ORDER BY rowid DESC
                                                       LIMIT 1
                                                       """;

    public const string SelectSourceByHashAndContext = "SELECT path AS Path, value AS Value FROM dbmem_content WHERE hash = @hash AND context = @context LIMIT 1";

    public const string SearchWithContext = """
                                            SELECT hash AS Hash, seq AS Seq, ranking AS Ranking, path AS Path, snippet AS Snippet
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

    public const string CountProjectEntries = """
                                              SELECT count(*)
                                              FROM dbmem_content
                                              WHERE context = @project
                                              """;

    public const string PendingCount = "SELECT memory_pending_count()";

    public const string ListFiles = "SELECT memory_list_files()";

    public const string IngestFile = "SELECT memory_add_file(@path, @context)";

    public const string IngestDirectory = "SELECT memory_add_directory(@path, @context)";

    public const string SetApiKey = "SELECT memory_set_apikey(@apiKey)";

    public const string SetModel = "SELECT memory_set_model(@provider, @model)";

    public const string SetDeferEmbeddings = "SELECT memory_set_option('defer_embeddings', @value)";

    public const string SetPreserveDuplicatePaths = "SELECT memory_set_option('preserve_duplicate_paths', @value)";

    public const string InsertContent = "SELECT memory_add_content(@path, @content, @context)";

    public const string EmbedPending = "SELECT memory_embed_pending(@limit)";

    public const string EmbedPendingAll = "SELECT memory_embed_pending()";

    public const string SelectEntriesByContext = """
                                                 SELECT hash AS Hash, path AS Path, context AS Context, value AS Value, created_at AS CreatedAt
                                                 FROM dbmem_content
                                                 WHERE context = @context
                                                 ORDER BY created_at DESC, rowid DESC
                                                 """;

    /// <summary>
    ///     The bank's committed contexts — shared plus every distinct project context (FR-MEM-1.16); workspaces excluded,
    ///     shared first.
    /// </summary>
    public const string CommittedContexts = """
                                            SELECT DISTINCT context
                                            FROM dbmem_content
                                            WHERE context = 'shared' OR context LIKE 'project:%'
                                            ORDER BY CASE WHEN context = 'shared' THEN 0 ELSE 1 END, context
                                            """;
}
