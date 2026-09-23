using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Embedding;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite.Memory;

/// <summary>
///     The read-path helpers <see cref="SearchAsync" /> composes — candidate windowing, the keyword
///     modality query, and the two settings the fusion reads. WP8's search seam
///     (docs/plans/2026-08-14-code-quality-improvement-plan.md), split out for the same reason as
///     <c>SqliteMemoryStore.Rows.cs</c>: the store's measured line ratchet
///     (<see cref="AiRaccoon.Tests.Unit.Storage.SqliteMemoryStoreSizeRatchetTests" />) says take a
///     seam rather than raise the cap.
/// </summary>
public sealed partial class SqliteMemoryStore
{
    /// <summary>
    ///     Per-modality candidate window before RRF fusion (see docs/adr/0006-rrf-parameter-optimization.md):
    ///     the default max(limit*3, 100) keeps overlap candidates ranked 20-100 from being
    ///     starved by a per-modality LIMIT.
    /// </summary>
    internal static int CandidateWindowFor(int limit, CandidateWindowMode mode = CandidateWindowMode.Max3X100) =>
        mode == CandidateWindowMode.Max5X50
            ? (int)Math.Clamp((long)limit * 5, 50, int.MaxValue)
            : (int)Math.Clamp((long)limit * 3, 100, int.MaxValue);

    /// <summary>A leg that was never queried is degradation, not disagreement (docs/adr/0078).</summary>
    private static IReadOnlyList<ModalityLeg> LegsFor(SearchParameters parameters,
        FtsQueryPlan plan,
        QueryVector queryVector,
        IReadOnlyList<MemorySearchResult> ftsCandidates,
        IReadOnlyList<MemorySearchResult> vectorCandidates
    ) =>
    [
        parameters.IsFtsQueried(plan) ? ModalityLeg.From("fts", ftsCandidates) : ModalityLeg.Skipped("fts"),
        parameters.IsVectorQueried(queryVector) ? ModalityLeg.From("vector", vectorCandidates) : ModalityLeg.Skipped("vector")
    ];


    /// <summary>
    ///     Keyword modality: FTS5 candidates without snippet() — deferred (see <see cref="BuildFtsResults" />).
    ///     carry each candidate's raw value, matching @query text and row id, for snippet resolution after ranking.
    /// </summary>
    private Task<IReadOnlyList<MemorySearchResult>> QueryFtsBatchAsync(
        SqliteConnection connection, SearchQuery query, SearchParameters parameters, FtsQueryPlan plan, QueryVector queryVector, ContextFilter contextFilter,
        ByHashIndex byHashIndex, CancellationToken cancellationToken)
    {
        var queryParameters = DynamicParameters.SearchParameters(query, parameters, plan, queryVector, contextFilter);
        return QueryFtsBatchAsync(connection, contextFilter, byHashIndex, plan.Expression, queryParameters, cancellationToken);
    }

    private Task<IReadOnlyList<MemorySearchResult>> QueryFallbackFtsBatchAsync(
        SqliteConnection connection, SearchQuery query, SearchParameters parameters, FtsQueryPlan plan, QueryVector queryVector, ContextFilter contextFilter,
        ByHashIndex byHashIndex, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(plan.Fallback);

        var queryParameters = DynamicParameters.FallbackSearchParameters(query, parameters, plan, queryVector, contextFilter);
        return QueryFtsBatchAsync(connection, contextFilter, byHashIndex, plan.Fallback, queryParameters, cancellationToken);
    }

    private async Task<IReadOnlyList<MemorySearchResult>> QueryFtsBatchAsync(SqliteConnection connection, ContextFilter contextFilter,
        ByHashIndex byHashIndex, string expression, DynamicParameters queryParameters, CancellationToken cancellationToken)
    {
        try
        {
            var rows = (await connection.QueryAsync<SearchRow>(
                    new CommandDefinition(
                        MemorySql.SearchByFilter.Replace("{filter}", contextFilter.Filter), queryParameters,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList();

            foreach (var row in rows)
            {
                byHashIndex.ValueByHash[row.Hash] = row.Value;
                byHashIndex.FtsQueryByHash[row.Hash] = expression;
                byHashIndex.IdByHash[row.Hash] = row.Id;
            }

            return BuildFtsResults(rows);
        }
        catch (SqliteException ex)
        {
            Log.KeywordModalityFailed(logger, ex);
            return [];
        }
    }

    /// <summary>
    ///     The anchor plan when some row in the bank is in the file the query names and matches its
    ///     section; otherwise the ordinary plan, so text that merely looks like file#section is searched as text.
    /// </summary>
    private static async Task<FtsQueryPlan> PlanAsync(SqliteConnection connection, string query, CancellationToken cancellationToken)
    {
        var ordinary = FtsQueryNormalizer.BuildPlan(query);
        var anchor = ordinary.AsPathQuery(query);
        if (anchor.AnchorFile is not { } anchorFile)
        {
            return ordinary;
        }

        var sourceFiles = await connection.QueryAsync<string?>(
            new CommandDefinition(MemorySql.SelectAnchorSourceFiles, new { query = anchor.Expression }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return sourceFiles.Any(sourceFile => SourcePathQuery.NamesFile(anchorFile, sourceFile)) ? anchor : ordinary;
    }

    /// <summary>
    ///     A file#section query names rows exactly, so the rows its anchor matched lead the fused
    ///     list (fused order kept within each group); the vector leg's view of a path string only
    ///     orders what follows.
    /// </summary>
    internal static IReadOnlyList<MemorySearchResult> AnchorMatchesFirst(
        IReadOnlyList<MemorySearchResult> fused, IReadOnlySet<string> anchorMatches) =>
        [.. fused.Where(result => anchorMatches.Contains(result.Hash)), .. fused.Where(result => !anchorMatches.Contains(result.Hash))];

    /// <summary>Maps FTS candidate rows to results with <see cref="MemorySearchResult.Snippet" /> left unresolved.</summary>
    internal static IReadOnlyList<MemorySearchResult> BuildFtsResults(IReadOnlyList<SearchRow> rows) =>
    [
        .. rows.Select(row => new MemorySearchResult(
            row.Hash, row.Ranking, row.Path, string.Empty,
            row.SourceFile, row.ChunkIndex, row.TotalChunks))
    ];
}
