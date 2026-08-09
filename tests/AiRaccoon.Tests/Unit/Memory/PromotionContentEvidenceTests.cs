using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ports scorer.py's doc_adjust() (docs/adr/0018-promotion-scoring-v2.md, round-3 lane-A):
/// evidence is zero-centred (rule-language, durability and portability can all go negative) rather
/// than a pile of one-sided bonuses, and the doc-index/turn-mirror evidence cap is gone — those
/// channels are hard-noise and never reach Evaluate() (see PromotionScorer).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionContentEvidenceTests
{
    private static readonly string[] AllProjects = ["proj-alpha", "proj-beta", "proj-gamma"];

    private static ContentEvidence Evaluate(string value, ProvenanceArchetype archetype = ProvenanceArchetype.WorkNote,
        string projectId = "proj-alpha") =>
        PromotionContentEvidence.Evaluate(value, archetype, projectId, AllProjects);

    private static readonly CandidateFeatures NeutralFeatures = new(
        RuleDensity: 0, MeasureWords: 0, NumUnit: 0, Ephemera: 0, Superseded: false,
        FindingRows: 0, TableFrac: 0, LinkDensity: 0, DocnameDensity: 0, VersionRows: 0,
        Frontmatter: false, NChars: 700, NWords: 110, MidSentence: false,
        TechBreadth: 0, XrefDensity: 0, ImpRuleDensity: 0,
        ForeignSubject: false, HeadingStart: false, StatusOpener: false, StatusVocab: 0,
        SecondPerson: false, CommitHashes: 0, RealMeasures: 0, DurableLoose: 0, DatedFact: false,
        FirstPerson: 0, MetaHeader: 0, Imperatives: 0, Urls: 0, ContentsIndex: false, DirReadme: false);

    [Fact]
    public void GeneralizableRuleLanguage_FiresPositiveAdjustment_WithReason()
    {
        var withRule = Evaluate("This is by design: never bypass the invalidation queue, it is a hard invariant.");
        var without = Evaluate("The queue processes items in the order they arrive at the front door.");

        withRule.Adjustment.ShouldBeGreaterThan(without.Adjustment);
        withRule.Reasons.ShouldContain("rule-language");
    }

    /// <summary>Round-3 lane-A centres rule-language: absent any rule wording, the term is a small
    /// negative (-0.20), not a no-op — a document channel's chunks spread around their prior instead
    /// of only ever being lifted by it.</summary>
    [Fact]
    public void NoRuleLanguage_IsAMildNegative_NotZero()
    {
        var noRule = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.WorkNote);

        noRule.Adjustment.ShouldBeLessThan(0.0);
    }

    /// <summary>"I cannot" / "we cannot" is first-person uncertainty, not a contract.</summary>
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

    [Fact]
    public void ForeignSubject_AddsExactlyFifteenHundredths()
    {
        var without = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.WorkNote);
        var with = PromotionContentEvidence.Evaluate(
            NeutralFeatures with { ForeignSubject = true }, ProvenanceArchetype.WorkNote);

        with.Adjustment.ShouldBe(without.Adjustment + 0.15, 0.0001);
    }

    /// <summary>Round-3 lane-A restores the heading-start bonus (scorer.py's `doc_adjust()`, dropped
    /// for exact-parity reasons and reinstated on measurement — see docs/adr/0018): a document chunk
    /// that opens on a markdown heading scores exactly 0.10 above an otherwise-identical chunk that
    /// does not.</summary>
    [Fact]
    public void HeadingStart_AddsExactlyTenHundredths()
    {
        var without = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.WorkNote);
        var with = PromotionContentEvidence.Evaluate(
            NeutralFeatures with { HeadingStart = true }, ProvenanceArchetype.WorkNote);

        with.Adjustment.ShouldBe(without.Adjustment + 0.10, 0.0001);
    }

    /// <summary>Round-3 lane-A flips v3's sign: a chunk starting mid-sentence is body prose, not a
    /// penalty — a chunk starting at a heading is disproportionately a section opener or a TOC.</summary>
    [Fact]
    public void MidSentenceOpener_AddsAPositiveAdjustment_NotAPenalty()
    {
        var without = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.WorkNote);
        var midSentence = PromotionContentEvidence.Evaluate(
            NeutralFeatures with { MidSentence = true }, ProvenanceArchetype.WorkNote);

        midSentence.Reasons.ShouldContain("mid-sentence");
        midSentence.Adjustment.ShouldBe(without.Adjustment + 0.15, 0.0001);
    }

    /// <summary>Mid-sentence is summed into the same evidence total as everything else and clamped
    /// together at the end — it does not apply after the ceiling has already absorbed it.</summary>
    [Fact]
    public void MidSentenceOpener_IsClampedTogetherWithEverythingElse_NotAppliedAfterTheCeiling()
    {
        var saturated = NeutralFeatures with
        {
            RuleDensity = 5.0, MeasureWords = 4, NumUnit = 4, NWords = 300, NChars = 2000,
            ForeignSubject = true, TechBreadth = 5
        };

        var atCeiling = PromotionContentEvidence.Evaluate(saturated, ProvenanceArchetype.Adr);
        var midSentence = PromotionContentEvidence.Evaluate(
            saturated with { MidSentence = true }, ProvenanceArchetype.Adr);

        atCeiling.Adjustment.ShouldBe(1.60, "the fixture must actually saturate Hi for this test to mean anything");
        midSentence.Adjustment.ShouldBe(1.60, "already at the ceiling, so +0.15 more still clamps to Hi");
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

    /// <summary>Plan-channel rule lift is capped low — plans quote gates as "must" without the fact
    /// being durable.</summary>
    [Fact]
    public void PlanArchetype_CapsRuleLanguageBonus()
    {
        var ruleLanguageText = "This is by design: never bypass the invalidation queue, it is a hard invariant " +
                                "recorded here so nobody has to relearn it, and the rule is required reading.";

        var asWorkNote = Evaluate(ruleLanguageText, ProvenanceArchetype.WorkNote);
        var asPlan = Evaluate(ruleLanguageText, ProvenanceArchetype.Plan);

        asPlan.Adjustment.ShouldBeLessThan(asWorkNote.Adjustment);
    }

    /// <summary>Portability (breadth of named third-party technology, less intra-repo cross-reference
    /// density) applies only to the considered-document family (adr, charter, explanation,
    /// measurement, research_synthesis, reference) — naming tools in a plan or review is enumerating
    /// what was inspected, not deciding about a technology.</summary>
    [Fact]
    public void Portability_OnlyAppliesToTheDocumentFamily()
    {
        var techHeavy = NeutralFeatures with { TechBreadth = 5 };

        var asAdr = PromotionContentEvidence.Evaluate(techHeavy, ProvenanceArchetype.Adr);
        var asPlan = PromotionContentEvidence.Evaluate(techHeavy, ProvenanceArchetype.Plan);
        var asAdrNoTech = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.Adr);

        asAdr.Reasons.ShouldContain("portability");
        asPlan.Reasons.ShouldNotContain("portability");
        asAdr.Adjustment.ShouldBeGreaterThan(asAdrNoTech.Adjustment);
    }

    /// <summary>Dense intra-repo cross-referencing (ADR-nn, issue #nn, §, NFR-x) is portability's
    /// mirror image and demotes a document-family chunk.</summary>
    [Fact]
    public void Portability_XrefDensity_DemotesADocumentFamilyChunk()
    {
        var withXrefs = NeutralFeatures with { XrefDensity = 5.0 };

        var without = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.Adr);
        var with = PromotionContentEvidence.Evaluate(withXrefs, ProvenanceArchetype.Adr);

        with.Adjustment.ShouldBeLessThan(without.Adjustment);
    }

    /// <summary>Durability (an impersonal is/are/means/requires/holds/applies ... never/always/only/not
    /// clause) is centred so the median chunk contributes nothing, and applies to every document
    /// channel, not just the portability-restricted family.</summary>
    [Fact]
    public void Durability_ImpersonalRuleClause_RaisesEveryDocumentChannel()
    {
        var withRule = NeutralFeatures with { ImpRuleDensity = 1.0 };

        var without = PromotionContentEvidence.Evaluate(NeutralFeatures, ProvenanceArchetype.Plan);
        var with = PromotionContentEvidence.Evaluate(withRule, ProvenanceArchetype.Plan);

        with.Reasons.ShouldContain("durable-rule-language");
        with.Adjustment.ShouldBeGreaterThan(without.Adjustment);
    }

    /// <summary>Substance ramp: a chunk near the corpus median length (110 words) contributes ~0; a
    /// fragment is pushed down, a full-bodied chunk is pushed up — replacing the old two cliff
    /// penalties at 420 chars / 60 words with a continuous ramp.</summary>
    [Fact]
    public void Substance_LongerChunk_ScoresHigherThanAFragment_AllElseEqual()
    {
        var fragment = NeutralFeatures with { NWords = 10 };
        var fullBodied = NeutralFeatures with { NWords = 250 };

        var fragmentEvidence = PromotionContentEvidence.Evaluate(fragment, ProvenanceArchetype.WorkNote);
        var fullBodiedEvidence = PromotionContentEvidence.Evaluate(fullBodied, ProvenanceArchetype.WorkNote);

        fullBodiedEvidence.Adjustment.ShouldBeGreaterThan(fragmentEvidence.Adjustment);
    }

    /// <summary>Auto-memory-note evidence never references MidSentence: the mid-sentence positive
    /// term is doc-channel-only.</summary>
    [Fact]
    public void MidSentenceOpener_DoesNotChangeAnAutoMemoryNoteScore()
    {
        var baseline = NeutralFeatures with { MeasureWords = 1, NumUnit = 1, ForeignSubject = true };

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
