using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.search;

/// <summary>
///     Dual-vector candidate construction must defer <see cref="SnippetFallback" /> to ranking
///     survivors rather than computing it for every candidate row (perf: SHA-256 + allocation
///     per row was previously unconditional).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SnippetLazinessTests
{
    private const string LongValue =
        "Architecture decision records capture the rationale behind significant technical choices. " +
        "Each record names the context, the decision itself, and the consequences that follow from it.";

    [Fact]
    public void BuildDualVectorResults_LeavesSnippetUncomputed_ForEveryCandidate()
    {
        var rows = Enumerable.Range(0, 25)
            .Select(i => new SqliteMemoryStore.VectorRow
            {
                Hash = $"hash-{i}",
                Seq = i,
                Path = $"p{i}.md",
                Value = LongValue,
                SourceFile = null,
                ChunkIndex = 0,
                TotalChunks = 1
            })
            .ToList();

        var results = SqliteMemoryStore.BuildDualVectorResults([.. rows.Select(r => (r, 0.0))]);

        results.Count.ShouldBe(25);
        results.ShouldAllBe(r => r.Snippet.Length == 0,
            "snippet computation must be deferred until a candidate survives ranking");
    }

    /// <summary>
    ///     Mirrors <see cref="BuildDualVectorResults_LeavesSnippetUncomputed_ForEveryCandidate" /> for
    ///     the FTS modality: FTS5's snippet() over the ~300-row candidate window costs ~6.3 ms/query
    ///     (docs/plans/2026-08-08-search-knn-perf.md WP7) — the same defect fixed for the vector modality.
    /// </summary>
    [Fact]
    public void BuildFtsResults_LeavesSnippetUncomputed_ForEveryCandidate()
    {
        var rows = Enumerable.Range(0, 25)
            .Select(i => new SqliteMemoryStore.SearchRow
            {
                Hash = $"hash-{i}",
                Seq = i,
                Ranking = 1.0 - i * 0.01,
                Path = $"p{i}.md",
                Value = LongValue,
                SourceFile = null,
                ChunkIndex = 0,
                TotalChunks = 1
            })
            .ToList();

        var results = SqliteMemoryStore.BuildFtsResults(rows);

        results.Count.ShouldBe(25);
        results.ShouldAllBe(r => r.Snippet.Length == 0,
            "snippet computation must be deferred until a candidate survives ranking");
    }

    /// <summary>Mirrors the plan's grep acceptance criterion: the candidate statement text must not carry the eager snippet() call.</summary>
    [Fact]
    public void SearchByFilter_DoesNotComputeSnippetEagerly()
    {
        MemorySql.SearchByFilter.Contains("snippet(", StringComparison.Ordinal).ShouldBeFalse(
            "snippet() must be deferred to ranking survivors, not computed for every FTS candidate row");
    }
}
