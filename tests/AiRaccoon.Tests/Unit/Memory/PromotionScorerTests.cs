using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Wires the archetype classifier, content evidence and organic refinement together
/// (docs/adr/0018-promotion-scoring-v2.md); component behaviors are covered by their own test files.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionScorerTests
{
    private static readonly string[] AllProjects = ["proj-alpha", "proj-beta"];
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static ExtractionCandidateRow Row(string path, string? sourceFile, string value, int accessCount = 0) =>
        new("h1", path, value, sourceFile, 0.5, accessCount, Now.AddDays(-5), null);

    [Fact]
    public void Reasons_LeadWithTheArchetypeTag()
    {
        var row = Row("docs/adr/0021-cache-invalidation.md", "docs/adr/0021-cache-invalidation.md",
            "The cache invalidation queue clears entries once their TTL expires, keeping memory pressure " +
            "predictable across long-running sessions without any operator input at all being required.");

        var (_, reasons) = PromotionScorer.Score(row, "proj-alpha", AllProjects);

        reasons[0].ShouldBe("adr");
    }

    [Fact]
    public void VeryShortChunk_UnderTwentyFiveWords_IsCappedRegardlessOfPrior()
    {
        var row = Row("docs/adr/0021-x.md", "docs/adr/0021-x.md", "A short decision.");

        var (score, _) = PromotionScorer.Score(row, "proj-alpha", AllProjects);

        score.ShouldBeLessThanOrEqualTo(0.60);
    }

    [Fact]
    public void OrganicEntry_GetsTheRefinementLayerApplied()
    {
        var statusDump = Row("h.md", null,
            "Done. Everything shipped to main, the full suite passed and CI is green across the whole " +
            "pipeline with nothing left in flight for this rollout, handoff notes are current and clean.");
        var durableFact = Row("h.md", null,
            "The retry queue must never exceed one in-flight message per partition; this is a contract " +
            "the dispatcher relies on and its precedence over batching is by design, not an accident.");

        var (statusScore, statusReasons) = PromotionScorer.Score(statusDump, "proj-alpha", AllProjects);
        var (durableScore, durableReasons) = PromotionScorer.Score(durableFact, "proj-alpha", AllProjects);

        statusReasons.ShouldContain("status-opener");
        durableScore.ShouldBeGreaterThan(statusScore);
    }

    [Fact]
    public void NonOrganicEntry_DoesNotGetRefinementReasons()
    {
        var row = Row("docs/work/notes.md", "docs/work/notes.md",
            "Done. Merged. Fixed. Shipped to main with the full suite passing across the whole pipeline.");

        var (_, reasons) = PromotionScorer.Score(row, "proj-alpha", AllProjects);

        reasons.ShouldNotContain("status-opener");
    }
}
