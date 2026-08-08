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

        var results = SqliteMemoryStore.BuildDualVectorResults(rows);

        results.Count.ShouldBe(25);
        results.ShouldAllBe(r => r.Snippet.Length == 0,
            "snippet computation must be deferred until a candidate survives ranking");
    }
}
