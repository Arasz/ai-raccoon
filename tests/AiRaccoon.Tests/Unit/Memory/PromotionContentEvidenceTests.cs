using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports agentB/scorer.py's features()/score_candidate() content-shape adjustment
/// (see docs/adr/0018-promotion-scoring-v2.md). The archetype passed in only matters for the
/// doc-index/turn-mirror positive-evidence cap.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionContentEvidenceTests
{
    private static readonly string[] AllProjects = ["proj-alpha", "proj-beta", "proj-gamma"];

    private static ContentEvidence Evaluate(string value, ProvenanceArchetype archetype = ProvenanceArchetype.WorkNote,
        string projectId = "proj-alpha", int accessCount = 0) =>
        PromotionContentEvidence.Evaluate(value, archetype, projectId, AllProjects, accessCount);

    [Fact]
    public void GeneralizableRuleLanguage_FiresPositiveAdjustment_WithReason()
    {
        var withRule = Evaluate("This is by design: never bypass the invalidation queue, it is a hard invariant.");
        var without = Evaluate("The queue processes items in the order they arrive at the front door.");

        withRule.Adjustment.ShouldBeGreaterThan(without.Adjustment);
        withRule.Reasons.ShouldContain("rule-language");
    }

    [Fact]
    public void MeasuredNumbersWithUnits_RequireBothTheWordAndTheUnit()
    {
        var withBoth = Evaluate("Measured wall time: dropped from 640ms to 210ms across the same harness.");
        var numbersOnly = Evaluate("The queue holds 640 items and 210 more arrived overnight from the harness.");

        withBoth.Reasons.ShouldContain("measured-values");
        numbersOnly.Reasons.ShouldNotContain("measured-values");
    }

    [Fact]
    public void ForeignProjectMention_IsProximityGated_NotBareSubstring()
    {
        var subject = Evaluate("proj-beta's pre-push gate rejects any commit missing a changelog entry " +
                                "for the release train, which keeps the history reviewable across every " +
                                "downstream consumer that depends on a clean, bisectable commit log.");
        var padding = string.Concat(Enumerable.Repeat("The release train keeps a changelog entry for every " +
                                                        "commit so history stays reviewable. ", 6));
        var mentionedLate = Evaluate(padding + "A practice later adopted by proj-beta as well, long after " +
                                                "this paragraph opened its case and said nothing about it.");

        subject.Reasons.ShouldContain("foreign-subject");
        mentionedLate.Reasons.ShouldNotContain("foreign-subject");
    }

    [Fact]
    public void HeadingStart_AddsPositiveAdjustment()
    {
        var heading = Evaluate("# Cache invalidation\nThe queue clears entries older than the configured TTL.");
        var midSentence = Evaluate("clears entries older than the configured TTL once the queue notices them.");

        heading.Reasons.ShouldContain("heading-start");
        heading.Adjustment.ShouldBeGreaterThan(midSentence.Adjustment);
    }

    [Fact]
    public void MidSentenceOpener_AddsPenalty()
    {
        var midSentence = Evaluate("clears entries older than the configured TTL once the queue notices them, " +
                                    "which keeps memory pressure predictable across long-running sessions.");

        midSentence.Reasons.ShouldContain("mid-sentence");
    }

    [Fact]
    public void AccessCount_AddsLogScaledBonus_Saturating()
    {
        const string text = "plain content with no particular shape to it at all here.";
        var unaccessed = Evaluate(text, accessCount: 0);
        var accessedFew = Evaluate(text, accessCount: 2);
        var accessedMany = Evaluate(text, accessCount: 500);
        var accessedEvenMore = Evaluate(text, accessCount: 5000);

        unaccessed.Reasons.ShouldNotContain("accessed");
        accessedFew.Reasons.ShouldContain("accessed");
        accessedFew.Adjustment.ShouldBeGreaterThan(unaccessed.Adjustment);
        accessedMany.Adjustment.ShouldBeGreaterThan(accessedFew.Adjustment);
        // saturating: two large-but-different access counts land on the same clamped bonus.
        accessedMany.Adjustment.ShouldBe(accessedEvenMore.Adjustment);
    }

    [Fact]
    public void PointerDensity_TableAndLinkHeavyChunk_IsPenalized()
    {
        var pointerHeavy = Evaluate(
            "| doc | path |\n| --- | --- |\n| [a](docs/a.md) | [b](docs/b.md) |\n| [c](docs/c.md) | [d](docs/d.md) |\n" +
            "| [e](docs/e.md) | [f](docs/f.md) |\n| [g](docs/g.md) | [h](docs/h.md) |");
        var prose = Evaluate("The cache invalidation queue clears entries once their TTL expires, keeping " +
                              "memory pressure predictable across long-running sessions without operator input.");

        pointerHeavy.Reasons.ShouldContain("pointer-density");
        pointerHeavy.Adjustment.ShouldBeLessThan(prose.Adjustment);
    }

    [Fact]
    public void FindingsRegisterRows_ArePenalized()
    {
        var register = Evaluate(
            "| G4 | latency regression | open |\n| D1 | flaky test | closed |\n| A13 | doc drift | open |\n" +
            "The table above tracks this review's open items across the sprint for the assigned owners.");

        register.Reasons.ShouldContain("finding-rows");
    }

    [Fact]
    public void InFlightCoordinationMarkers_ArePenalized()
    {
        var coordination = Evaluate(
            "AC: cache hit rate above 90%. Gate: perf review required before merge. Effort: 3 days. " +
            "Work happens on a worktree during Wave 2 of the rollout, dispatched to the perf lane.");

        coordination.Reasons.ShouldContain("ephemera");
    }

    [Fact]
    public void SupersededMarkers_ArePenalized()
    {
        var superseded = Evaluate(
            "This guidance was superseded by the 2026-05 sweep; the historical note below no longer applies " +
            "to the current cache layer and is kept only for archival context.");

        superseded.Reasons.ShouldContain("superseded");
    }

    [Fact]
    public void FrontmatterOnlyChunk_IsPenalized()
    {
        var frontmatterOnly = Evaluate("---\nupdated: 2026-05-01\n---\n\nSee the linked doc for details.");

        frontmatterOnly.Reasons.ShouldContain("frontmatter-only");
    }

    [Fact]
    public void DocIndexArchetype_CapsPositiveAdjustment()
    {
        var ruleLanguageText = "This is by design: never bypass the invalidation queue, it is a hard invariant " +
                                "recorded here so nobody has to relearn it, and the rule is required reading.";

        var asWorkNote = Evaluate(ruleLanguageText, ProvenanceArchetype.WorkNote);
        var asDocIndex = Evaluate(ruleLanguageText, ProvenanceArchetype.DocIndex);

        asDocIndex.Adjustment.ShouldBeLessThan(asWorkNote.Adjustment);
        asDocIndex.Adjustment.ShouldBeLessThanOrEqualTo(0.15);
    }

    [Fact]
    public void TurnMirrorArchetype_CapsPositiveAdjustment()
    {
        var ruleLanguageText = "This is by design: never bypass the invalidation queue, it is a hard invariant " +
                                "recorded here so nobody has to relearn it, and the rule is required reading.";

        var asTurnMirror = Evaluate(ruleLanguageText, ProvenanceArchetype.TurnMirror);

        asTurnMirror.Adjustment.ShouldBeLessThanOrEqualTo(0.15);
    }

    [Fact]
    public void Adjustment_IsClampedToTheDocumentedRange()
    {
        var veryNegative = Evaluate(
            "AC: x. Gate: x. Effort: x. Impact: x. worktree Wave 1 Wave 2 dispatched dispatched " +
            "superseded historical note no longer applies was reversed PR #123 issue #456 " +
            "| a | b |\n| c | d |\n[link](x.md) [link](y.md) [link](z.md)", accessCount: 0);

        veryNegative.Adjustment.ShouldBeGreaterThanOrEqualTo(-1.60);
    }
}
