using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports agentC/scorer.py's doc_adjust() (docs/adr/0018-promotion-scoring-v2.md, v3): content
/// evidence is shape-only now (no recency/access-count bonus) and the doc-index/turn-mirror evidence cap
/// is gone — those channels are hard-noise in v3 and never reach Evaluate() (see PromotionScorer).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionContentEvidenceTests
{
    private static readonly string[] AllProjects = ["proj-alpha", "proj-beta", "proj-gamma"];

    private static ContentEvidence Evaluate(string value, ProvenanceArchetype archetype = ProvenanceArchetype.WorkNote,
        string projectId = "proj-alpha") =>
        PromotionContentEvidence.Evaluate(value, archetype, projectId, AllProjects);

    [Fact]
    public void GeneralizableRuleLanguage_FiresPositiveAdjustment_WithReason()
    {
        var withRule = Evaluate("This is by design: never bypass the invalidation queue, it is a hard invariant.");
        var without = Evaluate("The queue processes items in the order they arrive at the front door.");

        withRule.Adjustment.ShouldBeGreaterThan(without.Adjustment);
        withRule.Reasons.ShouldContain("rule-language");
    }

    /// <summary>v3 change: "I cannot" / "we cannot" is first-person uncertainty, not a contract.</summary>
    [Fact]
    public void FirstPersonCannot_DoesNotCountAsRuleLanguage()
    {
        var firstPerson = Evaluate("I cannot promise this will hold under load, so treat it as a guess for now.");
        var thirdPerson = Evaluate("The service cannot exceed its configured memory budget under any load.");

        firstPerson.Reasons.ShouldNotContain("rule-language");
        thirdPerson.Reasons.ShouldContain("rule-language");
    }

    [Fact]
    public void MeasuredNumbersWithUnits_RequireBothTheWordAndTheUnit()
    {
        var withBoth = Evaluate("Measured wall time: dropped from 640ms to 210ms across the same harness.");
        var numbersOnly = Evaluate("The queue holds 640 items and 210 more arrived overnight from the harness.");

        withBoth.Reasons.ShouldContain("measured-values");
        numbersOnly.Reasons.ShouldNotContain("measured-values");
    }

    /// <summary>v3 addition: a durable rule backed by a verified measurement gets a combo bonus.</summary>
    [Fact]
    public void VerifiedMeasurementWithRuleLanguage_GetsAContractBonus()
    {
        var combo = Evaluate("Measured and verified: the queue must never exceed 640ms p95, by design.");
        var ruleOnly = Evaluate("The queue must never exceed a reasonable latency budget, by design, always.");

        combo.Reasons.ShouldContain("verified-contract");
        ruleOnly.Reasons.ShouldNotContain("verified-contract");
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

    /// <summary>The +0.10 many-projects bonus must carry its own tag so the reason list can tell it
    /// apart from the foreign-subject bonus — previously both effects reused the "foreign-subject" tag.</summary>
    [Fact]
    public void TwoForeignProjects_TagTheCountSeparatelyFromTheSubject()
    {
        var subjectAndCount = new CandidateFeatures(
            RuleDensity: 0, MeasureWords: 0, NumUnit: 0, Ephemera: 0, Superseded: false,
            FindingRows: 0, TableFrac: 0, LinkDensity: 0, DocnameDensity: 0, VersionRows: 0,
            Frontmatter: false, NChars: 500, NWords: 80, MidSentence: false, HeadingStart: false,
            ForeignProjects: 2, ForeignSubject: true, StatusOpener: false, StatusVocab: 0,
            SecondPerson: false, CommitHashes: 0, RealMeasures: 0, DurableLoose: 0, DatedFact: false,
            FirstPerson: 0, MetaHeader: 0, Imperatives: 0, Urls: 0, ContentsIndex: false, DirReadme: false);
        var countOnly = subjectAndCount with { ForeignSubject = false };

        var withSubject = PromotionContentEvidence.Evaluate(subjectAndCount, ProvenanceArchetype.WorkNote);
        var withoutSubject = PromotionContentEvidence.Evaluate(countOnly, ProvenanceArchetype.WorkNote);

        withSubject.Reasons.ShouldContain("foreign-subject");
        withSubject.Reasons.ShouldContain("many-foreign-projects");
        withoutSubject.Reasons.ShouldNotContain("foreign-subject");
        withoutSubject.Reasons.ShouldContain("many-foreign-projects");
        withSubject.Adjustment.ShouldBe(withoutSubject.Adjustment + 0.25, 0.0001);
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

    /// <summary>A fragment opening with markdown emphasis (e.g. "**minscore is measured...") is still a
    /// mid-sentence fragment once the leading markup is stripped — the raw first character alone missed it.</summary>
    [Fact]
    public void MidSentenceOpener_BehindLeadingMarkup_StillCountsAsMidSentence()
    {
        var midSentence = Evaluate("**minscore is measured inert to whatever the caller passes as a filter, " +
                                    "so it never trims results the way an agent expects it to.");

        midSentence.Reasons.ShouldContain("mid-sentence");
    }

    /// <summary>The mid-sentence penalty must survive the positive-evidence clamp: a row with enough
    /// other bonuses to already saturate Hi (before the penalty) must still come out 0.18 lower for
    /// opening mid-sentence, not have the penalty silently absorbed by the ceiling.</summary>
    [Fact]
    public void MidSentenceOpener_StillDemotesAChunkAlreadyAtTheCeiling()
    {
        var saturated = new CandidateFeatures(
            RuleDensity: 5.0, MeasureWords: 4, NumUnit: 4, Ephemera: 0, Superseded: false,
            FindingRows: 0, TableFrac: 0, LinkDensity: 0, DocnameDensity: 0, VersionRows: 0,
            Frontmatter: false, NChars: 2000, NWords: 300, MidSentence: false, HeadingStart: true,
            ForeignProjects: 0, ForeignSubject: true, StatusOpener: false, StatusVocab: 0,
            SecondPerson: false, CommitHashes: 0, RealMeasures: 0, DurableLoose: 0, DatedFact: false,
            FirstPerson: 0, MetaHeader: 0, Imperatives: 0, Urls: 0, ContentsIndex: false, DirReadme: false);

        var atCeiling = PromotionContentEvidence.Evaluate(saturated, ProvenanceArchetype.WorkNote);
        var midSentence = PromotionContentEvidence.Evaluate(
            saturated with { MidSentence = true, HeadingStart = false }, ProvenanceArchetype.WorkNote);

        atCeiling.Adjustment.ShouldBe(1.30, "the fixture must actually saturate Hi for this test to mean anything");
        midSentence.Reasons.ShouldContain("mid-sentence");
        midSentence.Adjustment.ShouldBe(1.30 - 0.18);
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
    public void FirstPersonNarrative_IsPenalized()
    {
        var narrative = Evaluate(
            "I spent the afternoon reading through my notes on the eviction code before I made any changes, " +
            "and I am still not sure my read of the shared lock is correct, so I left it for me to revisit.");
        var thirdPerson = Evaluate(
            "The afternoon went into reading through the eviction code before any changes were made, and the " +
            "read of the shared lock is not yet confirmed, so it was left for a later revisit by the team.");

        narrative.Reasons.ShouldContain("first-person");
        narrative.Adjustment.ShouldBeLessThan(thirdPerson.Adjustment);
    }

    [Fact]
    public void MetadataHeaderBlock_IsPenalized()
    {
        var metaHeavy = Evaluate(
            "**Task:** cache eviction sweep\n**Project:** ai-raccoon\n**Worktree:** feat/cache-sweep\n" +
            "**Date:** 2026-05-01\n\nThe sweep compares five eviction policies against the same replay trace.");

        metaHeavy.Reasons.ShouldContain("metadata-header");
    }

    [Fact]
    public void ImperativeChecklist_IsPenalized()
    {
        var checklist = Evaluate(
            "1. Review the eviction policy change\n2. Run the full benchmark suite\n3. Update the changelog " +
            "entry\nOnce all three are done the rollout can proceed to the next environment in the sequence.");

        checklist.Reasons.ShouldContain("imperative-checklist");
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

    /// <summary>v3 addition: plan-channel rule lift is capped low — plans quote gates as "must" without
    /// the fact being durable.</summary>
    [Fact]
    public void PlanArchetype_CapsRuleLanguageBonus()
    {
        var ruleLanguageText = "This is by design: never bypass the invalidation queue, it is a hard invariant " +
                                "recorded here so nobody has to relearn it, and the rule is required reading.";

        var asWorkNote = Evaluate(ruleLanguageText, ProvenanceArchetype.WorkNote);
        var asPlan = Evaluate(ruleLanguageText, ProvenanceArchetype.Plan);

        asPlan.Adjustment.ShouldBeLessThan(asWorkNote.Adjustment);
    }

    /// <summary>The mid-sentence penalty is doc-channel-only by decision (pending calibration
    /// fixtures): EvaluateAutoMemoryNote must never reference CandidateFeatures.MidSentence.</summary>
    [Fact]
    public void MidSentenceOpener_DoesNotChangeAnAutoMemoryNoteScore()
    {
        var baseline = new CandidateFeatures(
            RuleDensity: 0, MeasureWords: 1, NumUnit: 1, Ephemera: 0, Superseded: false,
            FindingRows: 0, TableFrac: 0, LinkDensity: 0, DocnameDensity: 0, VersionRows: 0,
            Frontmatter: false, NChars: 500, NWords: 80, MidSentence: false, HeadingStart: false,
            ForeignProjects: 0, ForeignSubject: true, StatusOpener: false, StatusVocab: 0,
            SecondPerson: false, CommitHashes: 0, RealMeasures: 0, DurableLoose: 0, DatedFact: false,
            FirstPerson: 0, MetaHeader: 0, Imperatives: 0, Urls: 0, ContentsIndex: false, DirReadme: false);

        var withoutMidSentence = PromotionContentEvidence.EvaluateAutoMemoryNote(baseline);
        var withMidSentence = PromotionContentEvidence.EvaluateAutoMemoryNote(baseline with { MidSentence = true });

        withMidSentence.Adjustment.ShouldBe(withoutMidSentence.Adjustment);
        withMidSentence.Reasons.ShouldNotContain("mid-sentence");
    }

    [Fact]
    public void Adjustment_IsClampedToTheDocumentedRange()
    {
        var veryNegative = Evaluate(
            "AC: x. Gate: x. Effort: x. Impact: x. worktree Wave 1 Wave 2 dispatched dispatched " +
            "superseded historical note no longer applies was reversed PR #123 issue #456 " +
            "| a | b |\n| c | d |\n[link](x.md) [link](y.md) [link](z.md)");

        veryNegative.Adjustment.ShouldBeGreaterThanOrEqualTo(-1.60);
    }
}
