using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Fusion;

/// <summary>
///     Why the no-fusion-regression rule produces an ORDER and not a score (docs/adr/0078,
///     appendix): injecting max(rrf, best single leg) into the fused score fails twice over — the
///     magnitude is rebuilt from position downstream, and equal scores hand the top slot to a
///     filename comparison.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ScoreInjectionTests
{
    private static MemorySearchResult Hit(string hash, string path) => new(hash, 0, path, "s");

    // Any design that gives two candidates the same fused score hands the top slot to
    // ThenBy(Path, Ordinal). Same two legs, same scores -- only the filenames differ.
    [Fact]
    public void EqualFusedScores_MakeTheTopResultAFunctionOfTheFilename()
    {
        var first = ReciprocalRankFusion.Fuse(
            [
                new WeightedResults([Hit("a", "zzz.md"), Hit("b", "aaa.md")], 1.0),
                new WeightedResults([Hit("b", "aaa.md"), Hit("a", "zzz.md")], 1.0)
            ],
            SearchQuery.DefaultRrfK, 0, 10);

        var renamed = ReciprocalRankFusion.Fuse(
            [
                new WeightedResults([Hit("a", "aaa.md"), Hit("b", "zzz.md")], 1.0),
                new WeightedResults([Hit("b", "zzz.md"), Hit("a", "aaa.md")], 1.0)
            ],
            SearchQuery.DefaultRrfK, 0, 10);

        first[0].Ranking.ShouldBe(first[1].Ranking, 1e-12);
        first[0].Hash.ShouldBe("b");
        renamed[0].Hash.ShouldBe("a");
    }

    // And an injected magnitude never survives: two wildly different score sets leave Merge identical.
    [Fact]
    public void InjectedMagnitude_IsDiscardedByTheSecondFusion()
    {
        var strong = (IReadOnlyList<MemorySearchResult>)
            [new MemorySearchResult("a", 1.0, "a.md", "s"), new MemorySearchResult("b", 0.99, "b.md", "s"), new MemorySearchResult("c", 0.98, "c.md", "s")];
        var weak = (IReadOnlyList<MemorySearchResult>)
            [new MemorySearchResult("a", 0.03, "a.md", "s"), new MemorySearchResult("b", 0.002, "b.md", "s"), new MemorySearchResult("c", 0.001, "c.md", "s")];

        SearchResultMerger.Merge(strong, 10).Select(r => r.Ranking)
            .ShouldBe(SearchResultMerger.Merge(weak, 10).Select(r => r.Ranking));
        SearchResultMerger.Merge(strong, 10).Select(r => r.Ranking)
            .ShouldBe([1.0, 61.0 / 62, 61.0 / 63], 1e-9);
    }
}
