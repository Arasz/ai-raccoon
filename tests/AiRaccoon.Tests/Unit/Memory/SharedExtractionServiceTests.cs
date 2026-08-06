using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SharedExtractionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] AllProjects = ["ai-raccoon", "job-search-ai-assistant", "ai-badger"];

    private readonly SharedExtractionService _service = new();

    private static ExtractionCandidateRow Row(
        string hash, string? sourceFile = null, string value = "some content", int accessCount = 0,
        double rating = 0.5, DateTimeOffset? createdAt = null, int? ttlDays = null) =>
        new(hash, $"{hash}.md", value, sourceFile, rating, accessCount, createdAt ?? Now.AddDays(-5),
            ttlDays);

    [Fact]
    public void Propose_OrganicWrite_RanksFirst_WithOrganicAndRecentReasons()
    {
        var rows = new[]
        {
            Row("a", sourceFile: "docs/x.md"),
            Row("b", sourceFile: null, value: "agent-written fact", accessCount: 0)
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Hash.ShouldBe("b");
        result.Candidates[0].Reasons.ShouldContain("organic-write");
        result.Candidates[0].Reasons.ShouldContain("recent");
        result.PromotedHashes.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_CrossProjectReference_AddsSignal()
    {
        var rows = new[] { Row("a", sourceFile: "docs/x.md", value: "fixes the job-search-ai-assistant tool chain") };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Reasons.ShouldContain("cross-project");
    }

    [Fact]
    public void Propose_OwnProjectIdInValue_IsNotCrossProject()
    {
        var rows = new[] { Row("a", sourceFile: "docs/x.md", accessCount: 1, value: "the ai-raccoon pipeline ships facts") };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Reasons.ShouldNotContain("cross-project");
    }

    [Fact]
    public void Propose_UsageSignal_AddsAccessedReason()
    {
        var rows = new[] { Row("a", sourceFile: "docs/x.md", accessCount: 2, rating: 0.6) };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Reasons.ShouldContain("accessed");
    }

    [Fact]
    public void Propose_RecentOnly_IsExcluded()
    {
        var rows = new[] { Row("a", sourceFile: "docs/x.md") };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_TtlRow_ExcludedByDefault_IncludedWhenFlagged()
    {
        var rows = new[] { Row("a", sourceFile: null, value: "ephemeral note", ttlDays: 30) };

        var excluded = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);
        excluded.Candidates.ShouldBeEmpty();

        var included = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], true, 20, Now);
        included.Candidates.ShouldHaveSingleItem();
        included.Candidates[0].Reasons.ShouldContain("ttl-row");
    }

    [Fact]
    public void Propose_LimitApplied_ByRankingScore()
    {
        var rows = new[]
        {
            Row("high", sourceFile: null, value: "organic fact about job-search-ai-assistant"), // 2 + 2 + 0.5
            Row("mid", sourceFile: null, value: "organic fact"),                                // 2 + 0.5
            Row("low", sourceFile: "docs/x.md", accessCount: 1)                                 // 1 + 0.5
        };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 2, Now);

        result.Candidates.Count.ShouldBe(2);
        result.Candidates[0].Hash.ShouldBe("high");
        result.Candidates[1].Hash.ShouldBe("mid");
    }

    [Fact]
    public void Propose_AlreadySharedValue_IsDeduplicated()
    {
        var rows = new[] { Row("a", sourceFile: null, value: "  already  shared fact ") };
        var sharedValues = new HashSet<string> { "alreadysharedfact" }; // whitespace-normalized

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            sharedValues, [], false, 20, Now);

        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_SharedPath_IsDeduplicated()
    {
        var rows = new[] { Row("a", sourceFile: null, value: "fact value") };
        var sharedPaths = new HashSet<string> { "shared/a.md" };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], sharedPaths, false, 20, Now);

        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void Promote_ReturnsRankedHashes_ExcludingDeduplicated()
    {
        var rows = new[]
        {
            Row("dup", sourceFile: null, value: "already there"),
            Row("keep", sourceFile: null, value: "fresh organic fact"),
            Row("low", sourceFile: "docs/x.md", accessCount: 1)
        };
        var sharedValues = new HashSet<string> { "alreadythere" };

        var result = _service.Run(ExtractMode.Promote, "ai-raccoon", AllProjects, rows,
            sharedValues, [], false, 20, Now);

        result.Candidates.Count.ShouldBe(2);
        result.Candidates[0].Hash.ShouldBe("keep");
        result.Candidates[1].Hash.ShouldBe("low");
        result.PromotedHashes.ShouldBe(["keep", "low"]);
    }

    [Fact]
    public void Run_EmptyRows_ReturnsEmptyResult()
    {
        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, [],
            [], [], false, 20, Now);

        result.Candidates.ShouldBeEmpty();
        result.PromotedHashes.ShouldBeEmpty();
    }

    [Fact]
    public void Propose_ValuePreview_IsTruncated()
    {
        var longValue = new string('x', 500);
        var rows = new[] { Row("a", sourceFile: null, value: longValue) };

        var result = _service.Run(ExtractMode.Propose, "ai-raccoon", AllProjects, rows,
            [], [], false, 20, Now);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].ValuePreview.Length.ShouldBeLessThanOrEqualTo(300);
    }
}
