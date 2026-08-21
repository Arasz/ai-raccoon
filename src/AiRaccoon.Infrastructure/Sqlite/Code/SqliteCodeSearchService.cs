using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite.Code;

/// <summary>
///     Hybrid code search (WP5, docs/work/2026-08-21-code-search-implementation-plan.md §3.6):
///     FTS5 (code_fts) + vec0 (vec_code, ctx = project_id) fused by the same weighted
///     reciprocal-rank-fusion shape as memory's own fusion (weight / (k + rank), max-normalized —
///     <see cref="AiRaccoon.Infrastructure.Sqlite.Memory.ReciprocalRankFusion" />), computed inline
///     here rather than shared, since Fuse operates on MemorySearchResult and the two result shapes
///     deliberately do not unify (code carries line ranges, not chunk/source metadata). RrfK/FtsWeight/
///     VectorWeight resolve through <see cref="SearchParameters.FromSources" /> from the SAME
///     retrieval.* bank settings memory search tunes (§12.2 H7); retrieval.structureAlpha resolves
///     but is never read here — code has no structure modality (§3.1), so it cannot move ranking.
///     Degrades to FTS5-only with <see cref="CodeSearchWarnings.EngineNotConfigured" /> when no code
///     engine is configured; a configured-but-unloadable engine's
///     <see cref="CodeEngineUnloadableException" /> propagates as-is — code searches refuse
///     actionably, memory searches (a wholly separate engine/settings row) are unaffected.
/// </summary>
public sealed class SqliteCodeSearchService(ISqliteConnectionFactory factory, ICodeEmbedder embedder) : ICodeSearchService
{
    public async Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(query);

        var plan = FtsQueryNormalizer.BuildPlan(query.Query);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var parameters = SearchParameters.FromSources(query,
            await ReadRetrievalDefaultsAsync(connection, cancellationToken).ConfigureAwait(false));

        // Embedded (and possibly trimmed) BEFORE the FTS/vector legs run: an unloadable engine
        // must refuse the whole search, not silently degrade to a partial FTS-only result.
        var queryVector = await embedder.EmbedQueryAsync(connection, query.Query, cancellationToken)
            .ConfigureAwait(false);

        var window = SqliteMemoryStore.CandidateWindowFor(query.Limit, parameters.CandidateWindow);

        var ftsRows = plan.Expression.Length == 0
            ? []
            : await QueryFtsAsync(connection, query, plan, window, cancellationToken).ConfigureAwait(false);
        var vectorRows = queryVector.IsEmpty
            ? []
            : await QueryVectorAsync(connection, query, queryVector, window, cancellationToken).ConfigureAwait(false);

        var fused = Fuse(ftsRows, vectorRows, parameters, query);
        var warning = queryVector.IsEmpty
            ? CodeSearchWarnings.EngineNotConfigured
            : queryVector.Trimmed
                ? CodeSearchWarnings.QueryTrimmedToCodeWindow
                : null;

        return new CodeSearchResults(fused, warning);
    }

    public async Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);
        Guard.IsNotNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<CodeEntryRow?>(new CommandDefinition(
            """
            SELECT hash AS Hash, value AS Value, path AS Path, line_start AS LineStart, line_end AS LineEnd
            FROM code_entries
            WHERE project_id = @projectId AND hash = @hash
            ORDER BY id
            LIMIT 1
            """,
            new { projectId, hash }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null ? null : new CodeEntry(row.Hash, row.Value, row.Path, row.LineStart, row.LineEnd);
    }

    private static async Task<IReadOnlyList<CodeFtsRow>> QueryFtsAsync(SqliteConnection connection, CodeSearchQuery query,
        FtsQueryPlan plan, int limit, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<CodeFtsRow>(new CommandDefinition(
            """
            SELECT e.hash AS Hash, e.path AS Path,
                   snippet(code_fts, 0, '', '', '…', 12) AS Snippet,
                   e.line_start AS LineStart, e.line_end AS LineEnd
            FROM code_fts
            JOIN code_entries e ON e.id = code_fts.rowid
            WHERE code_fts MATCH @match AND e.project_id = @projectId
            ORDER BY bm25(code_fts)
            LIMIT @limit
            """,
            new { match = plan.Expression, projectId = query.ProjectId, limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

    /// <summary>vec0 KNN over the ctx (= project_id) partition — mirrors MemorySql.VectorSearchByFilter's shape.</summary>
    private static async Task<IReadOnlyList<CodeVectorRow>> QueryVectorAsync(SqliteConnection connection,
        CodeSearchQuery query, QueryVector queryVector, int limit, CancellationToken cancellationToken) =>
        (await connection.QueryAsync<CodeVectorRow>(new CommandDefinition(
            """
            SELECT e.hash AS Hash, e.path AS Path, e.value AS Value,
                   e.line_start AS LineStart, e.line_end AS LineEnd, v.distance AS Distance
            FROM vec_code v
            JOIN code_entries e ON e.id = v.rowid
            WHERE v.ctx = @ctx AND v.embedding MATCH @queryVector AND k = @limit
            ORDER BY v.distance, e.path
            """,
            new { ctx = query.ProjectId, queryVector = queryVector.Data, limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

    /// <summary>
    ///     Weighted RRF fusion: score = sum of weight / (k + rank) per contributing list,
    ///     max-normalized so the top hit is always 1.0 (hybrid-score-relative — §12.6: replaces the
    ///     old FTS5-only leg's positional (k+1)/(k+rank) placeholder). The first list carrying a hit
    ///     supplies its payload, so the FTS leg's highlighted snippet() wins over the vector leg's
    ///     full-value fallback when both legs match the same row.
    /// </summary>
    private static IReadOnlyList<CodeSearchResult> Fuse(IReadOnlyList<CodeFtsRow> ftsRows,
        IReadOnlyList<CodeVectorRow> vectorRows, SearchParameters parameters, CodeSearchQuery query)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var payloads = new Dictionary<string, CodeSearchResult>(StringComparer.Ordinal);

        AddList(scores, payloads, ftsRows.Count,
            i => (ftsRows[i].Hash, new CodeSearchResult(ftsRows[i].Hash, 0, ftsRows[i].Path, ftsRows[i].Snippet,
                ftsRows[i].LineStart, ftsRows[i].LineEnd)),
            parameters.FtsWeight, parameters.RrfK);
        AddList(scores, payloads, vectorRows.Count,
            i => (vectorRows[i].Hash, new CodeSearchResult(vectorRows[i].Hash, 0, vectorRows[i].Path,
                FallbackSnippet(vectorRows[i].Value), vectorRows[i].LineStart, vectorRows[i].LineEnd)),
            parameters.VectorWeight, parameters.RrfK);

        if (scores.Count == 0)
        {
            return [];
        }

        var max = scores.Values.Max();
        return
        [
            .. scores
                .Select(pair => payloads[pair.Key] with { Ranking = pair.Value / max })
                .Where(result => result.Ranking >= query.MinRelativeScore)
                .OrderByDescending(result => result.Ranking)
                .ThenBy(result => result.Path, StringComparer.Ordinal)
                .Take(query.Limit)
        ];
    }

    private static void AddList(Dictionary<string, double> scores, Dictionary<string, CodeSearchResult> payloads,
        int count, Func<int, (string Hash, CodeSearchResult Payload)> at, int weight, int k)
    {
        for (var i = 0; i < count; i++)
        {
            var (hash, payload) = at(i);
            var rank = i + 1;
            scores[hash] = scores.GetValueOrDefault(hash) + ((double)weight / (k + rank));
            payloads.TryAdd(hash, payload);
        }
    }

    private static string FallbackSnippet(string value) => value.Length <= 200 ? value : value[..200] + "…";

    /// <summary>
    ///     Mirrors SqliteMemoryStore.GetSearchParameterDefaultsAsync's settings-snapshot shape (not
    ///     shared — that one is internal to a different partial class): one batched retrieval.* read,
    ///     absent/malformed keys parse to null so FromSources falls back to the canonical constants.
    /// </summary>
    private static async Task<ISearchParametersSource> ReadRetrievalDefaultsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = (await connection.QueryAsync<SettingRow>(new CommandDefinition(
                MemorySql.SelectSettingsByPrefix, new { prefix = "retrieval." }, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal);
        return new RetrievalSettingsSource(rows);
    }

    /// <summary>A sparse settings snapshot: null per option when the key is absent or malformed.</summary>
    private sealed class RetrievalSettingsSource(IReadOnlyDictionary<string, string> retrieval) : ISearchParametersSource
    {
        public int? RrfK => SearchParameterSettingsKeys.ParseNullableInt(Get(SearchParameterSettingsKeys.RrfK), 1);
        public int? FtsWeight => SearchParameterSettingsKeys.ParseNullableInt(Get(SearchParameterSettingsKeys.FtsWeight), 0);
        public int? VectorWeight => SearchParameterSettingsKeys.ParseNullableInt(Get(SearchParameterSettingsKeys.VectorWeight), 0);
        public double? SourceLambda => null;
        public double? ConsolidationThreshold => null;
        public DocScoreFormula? DocScoreFormula => null;
        public CandidateWindowMode? CandidateWindow => SearchParameterSettingsKeys.ParseCandidateWindow(
            Get(SearchParameterSettingsKeys.CandidateWindow));
        public double? StructureAlpha => null; // deliberately never resolved from settings here (§12.2 H7): code has no structure modality
        public bool? FusionNoRegressionEnabled => null;

        private string? Get(string key) => retrieval.GetValueOrDefault(key);
    }

    private sealed record SettingRow(string Key, string Value);

    private sealed class CodeFtsRow
    {
        public string Hash { get; init; } = "";

        public string Path { get; init; } = "";

        public string Snippet { get; init; } = "";

        public int LineStart { get; init; }

        public int LineEnd { get; init; }
    }

    private sealed class CodeVectorRow
    {
        public string Hash { get; init; } = "";

        public string Path { get; init; } = "";

        public string Value { get; init; } = "";

        public int LineStart { get; init; }

        public int LineEnd { get; init; }
    }

    private sealed class CodeEntryRow
    {
        public string Hash { get; init; } = "";

        public string Value { get; init; } = "";

        public string Path { get; init; } = "";

        public int LineStart { get; init; }

        public int LineEnd { get; init; }
    }
}
