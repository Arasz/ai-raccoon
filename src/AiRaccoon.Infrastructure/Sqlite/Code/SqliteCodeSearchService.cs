using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using CommunityToolkit.Diagnostics;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite.Code;

/// <summary>
///     FTS5-only code search (v1 of the WP5 hybrid): queries code_fts, project-scoped only. No
///     vec0 leg, no RRF fusion, no manifest-aware query trim — those land where WP5 replaces this
///     leg (docs/work/2026-08-21-code-search-implementation-plan.md §3.4/§3.6). Ranking uses the
///     same reciprocal-rank-fusion normalization as a single-list memory RRF fuse, so rank 1 is
///     always 1.0 (<see cref="AiRaccoon.Infrastructure.Sqlite.Memory.ReciprocalRankFusion" />) —
///     computed inline here rather than shared, since Fuse operates on MemorySearchResult and the
///     two result shapes deliberately do not unify (code carries line ranges, not chunk/source metadata).
/// </summary>
public sealed class SqliteCodeSearchService(ISqliteConnectionFactory factory) : ICodeSearchService
{
    public async Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(query);

        var plan = FtsQueryNormalizer.BuildPlan(query.Query);
        if (plan.Expression.Length == 0)
        {
            return new CodeSearchResults([]);
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = (await connection.QueryAsync<CodeSearchRow>(new CommandDefinition(
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
            new { match = plan.Expression, projectId = query.ProjectId, limit = query.Limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var results = new List<CodeSearchResult>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var rank = i + 1;
            var ranking = (SearchQuery.DefaultRrfK + 1.0) / (SearchQuery.DefaultRrfK + rank);
            if (ranking < query.MinRelativeScore)
            {
                continue;
            }

            var row = rows[i];
            results.Add(new CodeSearchResult(row.Hash, ranking, row.Path, row.Snippet, row.LineStart, row.LineEnd));
        }

        return new CodeSearchResults(results);
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

    private sealed class CodeSearchRow
    {
        public string Hash { get; init; } = "";

        public string Path { get; init; } = "";

        public string Snippet { get; init; } = "";

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
