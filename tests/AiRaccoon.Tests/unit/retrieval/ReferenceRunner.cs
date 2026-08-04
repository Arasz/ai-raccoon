using AiRaccoon.Benchmarks.Corpus;
using AiRaccoon.Tests.Retrieval.Assets;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Tests.Retrieval.Reference;

/// <summary>One hit from the reference extension's memory_search virtual table.</summary>
public sealed record ReferenceHit(string Hash, int Seq, double Ranking, string Path, string Snippet);

/// <summary>Deterministic reference top-k output for one query.</summary>
public sealed record ReferenceQueryResult(string QueryId, string QueryText, IReadOnlyList<ReferenceHit> Hits);

/// <summary>A complete reference run over the corpus and query set.</summary>
public sealed record ReferenceRun(
    string Engine,
    string Model,
    string Context,
    int K,
    double MinRanking,
    int DocumentCount,
    IReadOnlyList<ReferenceQueryResult> ResultsByQuery);

/// <summary>
///     Runs the pinned sqlite-memory 1.3.5 extension (the reference oracle) against a temp
///     memory.db. Self-contained: its own inline SQL mirrors the extension surface and does not
///     touch src/AiRaccoon.Infrastructure/Sqlite, whose rewrite (P1) must not move this oracle.
///     Deferral defaults on; embeddings run explicitly after the corpus load, so output is
///     deterministic (same model file, same machine, same corpus — same rankings).
/// </summary>
public static class ReferenceRunner
{
    public const string Context = "harness";
    public const int DefaultK = 10;
    public const double MinRanking = 0.0;

    private const string AddContentSql = "SELECT memory_add_content(@path, @content, @context)";
    private const string SetModelSql = "SELECT memory_set_model('local', @model)";
    private const string SetOptionSql = "SELECT memory_set_option(@key, @value)";
    private const string EmbedPendingSql = "SELECT memory_embed_pending()";
    private const string CountSql = "SELECT count(*) FROM dbmem_content";

    private const string SearchSql = """
                                     SELECT hash AS Hash, seq AS Seq, ranking AS Ranking, path AS Path, snippet AS Snippet
                                     FROM memory_search
                                     WHERE query = @query
                                       AND context = @context
                                       AND ranking >= @minScore
                                     ORDER BY ranking DESC, seq ASC
                                     LIMIT @limit
                                     """;

    public static async Task<ReferenceRun> RunAsync(
        IReadOnlyList<CorpusDocument> documents,
        IReadOnlyList<CorpusQuery> queries,
        int k = DefaultK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(queries);
        if (k <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(k));
        }

        var dbPath = Path.Combine(Path.GetTempPath(), "ai-raccoon-reference", $"memory-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        try
        {
            await using var connection = new SqliteConnection($"Data Source={dbPath}");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.EnableExtensions();
            connection.LoadExtension(ReferenceAssets.VectorModulePath);
            connection.LoadExtension(ReferenceAssets.MemoryModulePath);

            // Oracle configuration mirrors the production factory (FR-MEM-1.12): deferral on,
            // path-scoped hashes on. Rows stay pending until the explicit embed below.
            SetOption(connection, "defer_embeddings", 1);
            SetOption(connection, "preserve_duplicate_paths", 1);

            // The extension filters below min_score 0.7 by default and caps rows at 20, which
            // would leave most corpus queries with zero or truncated results. The golden needs
            // the full top-k, so the oracle captures everything and the harness applies its own
            // cutoffs (k sweep up to 60). Hybrid weights stay at extension defaults (0.6/0.4).
            SetOption(connection, "min_score", 0);
            SetOption(connection, "max_results", 200);

            foreach (var doc in documents)
            {
                ExecuteScalar(connection, AddContentSql,
                    ("@path", doc.Id), ("@content", doc.Text), ("@context", Context));
            }

            var stored = ExecuteScalar<long>(connection, CountSql);
            if (stored != documents.Count)
            {
                throw new InvalidOperationException(
                    $"Reference corpus load incomplete: {stored} rows for {documents.Count} documents.");
            }

            ExecuteScalar(connection, SetModelSql, ("@model", ReferenceAssets.ModelPath));
            ExecuteScalar(connection, EmbedPendingSql);

            var results = new List<ReferenceQueryResult>(queries.Count);
            foreach (var query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new ReferenceQueryResult(query.Id, query.Text, QueryHits(connection, query.Text, k)));
            }

            return new ReferenceRun(
                "sqlite-memory-1.3.5+sqlite-vector-1.0.0",
                Path.GetFileName(ReferenceAssets.ModelPath),
                Context,
                k,
                MinRanking,
                documents.Count,
                results);
        }
        finally
        {
            DeleteQuietly(dbPath);
            DeleteQuietly($"{dbPath}-wal");
            DeleteQuietly($"{dbPath}-shm");
        }
    }

    private static List<ReferenceHit> QueryHits(SqliteConnection connection, string query, int limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SearchSql;
        command.Parameters.AddWithValue("@query", query);
        command.Parameters.AddWithValue("@context", Context);
        command.Parameters.AddWithValue("@minScore", MinRanking);
        command.Parameters.AddWithValue("@limit", limit);

        var hits = new List<ReferenceHit>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            hits.Add(new ReferenceHit(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetDouble(2),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return hits;
    }

    private static void SetOption(SqliteConnection connection, string key, long value)
    {
        ExecuteScalar(connection, SetOptionSql, ("@key", key), ("@value", value));
    }

    private static void ExecuteScalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteScalar();
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // temp cleanup is best-effort
        }
    }
}
