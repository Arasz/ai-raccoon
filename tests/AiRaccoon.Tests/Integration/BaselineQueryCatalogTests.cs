using System.Text.Json;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Wave 5a data pins: difficulty strata + relevance grades per the rubric in
///     docs/plans/retrieval-improvement-c.md §3 Wave 5a.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Retrieval)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BaselineQueryCatalogTests
{
    private const string ExpectedCatalog =
        "A1 A10 A2 A3 A4 A5 A6 A7 A8 A9 B1 B2 B3 B4 B5 B6 C1 C2 C3 C4 C5 C6 D1 D2 D3 D4 E1 E2 E3 F1 F2 F3 G1 G2 G3 H1 H2 H3 S1 S2 S3 S4 S5 S6";

    private static readonly string[] Strata = ["easy", "medium", "hard", "very-hard"];

    [Fact]
    public void AllQueries_UniqueIds_MatchCommittedCatalog()
    {
        var queries = LoadQueries();

        queries.Length.ShouldBe(44, "the committed baseline catalog has 44 queries (Wave 5b added A8-A10 + S1/S3-S6)");
        string.Join(" ", queries.Select(q => q.Id).OrderBy(id => id, StringComparer.Ordinal))
            .ShouldBe(ExpectedCatalog);
        queries.Select(q => q.Id).Distinct().Count().ShouldBe(queries.Length, "query ids must be unique");
    }

    [Fact]
    public void AllQueries_HaveDifficultyStratum_AllStrataUsed()
    {
        var queries = LoadQueries();

        foreach (var query in queries)
        {
            Strata.ShouldContain(query.Difficulty, $"{query.Id} must carry a difficulty stratum");
        }

        // Stratification is only meaningful if every stratum is populated.
        foreach (var stratum in Strata)
        {
            queries.Count(q => q.Difficulty == stratum).ShouldBeGreaterThanOrEqualTo(3,
                $"stratum '{stratum}' must hold at least 3 queries");
        }

        // Excluded-content negative tests are the very-hard stratum by design.
        foreach (var negative in queries.Where(q => q.NegativeTest))
        {
            negative.Difficulty.ShouldBe("very-hard", $"{negative.Id}: excluded content is hardest to retrieve");
        }
    }

    [Fact]
    public void ExpectedSourceQueries_DifficultyMatchesMeasuredHybridRanks()
    {
        var queries = LoadQueries();

        // Difficulty strata reflect the measured hybrid exact-chunk rank at delivery
        // (docs/plans/retrieval-improvement-c.md, Wave 5a/5b "Delivered" sections).
        var expected = new Dictionary<string, string>
        {
            ["A2"] = "easy", ["A5"] = "easy", ["C1"] = "easy", ["C2"] = "easy", ["C5"] = "easy",
            ["A8"] = "easy", ["S3"] = "easy", ["S4"] = "easy", ["S6"] = "easy",
            ["A1"] = "medium", ["A3"] = "medium", ["A9"] = "medium", ["A10"] = "medium",
            ["S1"] = "medium", ["S5"] = "medium",
            ["A4"] = "hard", ["A7"] = "hard", ["S2"] = "hard",
            ["A6"] = "very-hard",
        };

        foreach (var (id, stratum) in expected)
        {
            QueryById(queries, id).Difficulty.ShouldBe(stratum, $"{id} difficulty");
        }
    }

    [Fact]
    public void ExpectedSourceQueries_HaveRelevanceGrade()
    {
        var queries = LoadQueries();

        foreach (var query in queries.Where(q => q.ExpectedSource is not null))
        {
            query.RelevanceGrade.ShouldBeInRange(1, 5, $"{query.Id} must be graded 1-5");
        }

        foreach (var query in queries.Where(q => q.ExpectedSource is null))
        {
            query.RelevanceGrade.ShouldBe(0, $"{query.Id}: coverage queries are not scored");
        }

        // Relevance-grade rubric (docs/plans/retrieval-improvement-c.md, Wave 5a "Delivered"):
        // 5 = the chunk alone fully answers; 4 = the answer spans 2+ authoritative chunks/files.
        var expected = new Dictionary<string, int>
        {
            ["A1"] = 5, ["A2"] = 5, ["A3"] = 5, ["A4"] = 5, ["A5"] = 5,
            ["A6"] = 4, ["A7"] = 4, ["A8"] = 5, ["A9"] = 5, ["A10"] = 5,
            ["S1"] = 5, ["S2"] = 5, ["S3"] = 5, ["S4"] = 5, ["S5"] = 4, ["S6"] = 5,
            ["C1"] = 5, ["C2"] = 5, ["C5"] = 5,
        };

        foreach (var (id, grade) in expected)
        {
            QueryById(queries, id).RelevanceGrade.ShouldBe(grade, $"{id} relevance grade");
        }
    }

    [Fact]
    public void NegativeTests_H1H3_NonEvidentialAndUngraded()
    {
        var queries = LoadQueries();
        var negatives = queries.Where(q => q.NegativeTest).ToArray();

        negatives.Length.ShouldBe(3, "H1-H3 are the committed negative tests");
        negatives.Select(q => q.Id).OrderBy(id => id, StringComparer.Ordinal).ShouldBe(["H1", "H2", "H3"]);

        foreach (var negative in negatives)
        {
            negative.ExpectedSource.ShouldBeNull($"{negative.Id}: negative tests carry no expected source");
            negative.RelevanceGrade.ShouldBe(0, $"{negative.Id}: non-evidential, not scored (comparison-clean.md)");
            negative.Difficulty.ShouldBe("very-hard", $"{negative.Id}: the corpus must exclude the target content");
        }
    }

    private static BaselineQuery QueryById(IReadOnlyList<BaselineQuery> queries, string id) =>
        queries.First(q => q.Id == id);

    private static BaselineQuery[] LoadQueries()
    {
        var projectRoot = FindProjectRoot();
        var queriesPath = Path.Combine(projectRoot, "scripts", "baseline-queries.json");
        if (!File.Exists(queriesPath))
        {
            throw new InvalidOperationException($"Missing {queriesPath} — the baseline query catalog.");
        }

        return JsonSerializer.Deserialize<BaselineQuery[]>(File.ReadAllText(queriesPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private sealed record BaselineQuery(
        string Id,
        string Category,
        string Query,
        string? ExpectedSource,
        string? ExpectedKnowledge,
        string Scope,
        int SearchLimit,
        bool NegativeTest,
        string? Difficulty = null,
        int RelevanceGrade = 0);
}
