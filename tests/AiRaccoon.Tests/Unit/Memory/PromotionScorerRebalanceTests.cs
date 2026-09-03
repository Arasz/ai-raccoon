using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>Ablation-pair rebalance (ADR-0095): sparse rule phrasing no longer tags or pays like
/// sustained rule prose, and the rule bonus follows the rebalanced coefficients.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionScorerRebalanceTests
{
    private static readonly CandidateFeatures Neutral = new(
        RuleDensity: 0, MeasureWords: 0, NumUnit: 0, Ephemera: 0, Superseded: false,
        FindingRows: 0, TableFrac: 0, LinkDensity: 0, DocnameDensity: 0, VersionRows: 0,
        Frontmatter: false, NChars: 700, NWords: 110, MidSentence: false,
        TechBreadth: 0, XrefDensity: 0, ImpRuleDensity: 0,
        ForeignSubject: false, HeadingStart: false, StatusOpener: false, StatusVocab: 0,
        SecondPerson: false, CommitHashes: 0, RealMeasures: 0, DurableLoose: 0, DatedFact: false,
        FirstPerson: 0, MetaHeader: 0, Imperatives: 0, Urls: 0, ContentsIndex: false, DirReadme: false);

    private static readonly string[] AllProjects = ["proj-alpha", "proj-beta", "proj-gamma"];

    private static ContentEvidence Evaluate(string value) =>
        PromotionContentEvidence.Evaluate(value, ProvenanceArchetype.WorkNote, "proj-alpha", AllProjects);

    private static ContentEvidence EvaluateFeatures(CandidateFeatures f) =>
        PromotionContentEvidence.Evaluate(f, ProvenanceArchetype.WorkNote);

    /// <summary>A lone rule-flavoured sentence in long prose is incidental phrasing, not a durable
    /// rule: it stays untagged and scores below sustained rule prose. Red direction: the old
    /// gate tags any density above zero, so the untagged assertion fails before the fix.</summary>
    [Fact]
    public void SparseRuleMention_StaysUntagged_AndScoresBelowSustainedRuleProse()
    {
        var filler = string.Concat(Enumerable.Repeat(
            "The queue processes items in arrival order at the front door, keeping memory pressure " +
            "steady across long sessions with no operator input. ", 12));
        var sparse = Evaluate(filler + "Never bypass the invalidation queue.");
        var sustained = Evaluate(
            filler + "Never bypass the invalidation queue. This limit is a hard invariant recorded " +
            "here for every future reader.");

        sparse.Reasons.ShouldNotContain("rule-language");
        sustained.Reasons.ShouldContain("rule-language");
        sparse.Adjustment.ShouldBeLessThan(sustained.Adjustment);
    }

    /// <summary>Feature-level pin for the same gate: density 0.4 stays silent while the 0.5
    /// boundary still tags. Red direction: the old gate tags 0.4, so the silence assertion
    /// fails before the fix.</summary>
    [Fact]
    public void RuleGate_FiresAtHalfDensity_NotAtAnyMatch()
    {
        var sparse = PromotionContentEvidence.Evaluate(
            Neutral with { RuleDensity = 0.4 }, ProvenanceArchetype.WorkNote);
        var boundary = PromotionContentEvidence.Evaluate(
            Neutral with { RuleDensity = 0.5 }, ProvenanceArchetype.WorkNote);

        sparse.Adjustment.ShouldBeGreaterThan(-1.0, "headroom: the silence must be the gate, not the floor");
        sparse.Reasons.ShouldNotContain("rule-language");
        boundary.Reasons.ShouldContain("rule-language");
    }

    /// <summary>Exact diff of the rebalanced rule formula at density 2.0: the bonus over the
    /// no-rule baseline is (0.30*2 - 0.25) - (-0.25) = 0.60. Red direction: the old
    /// coefficients pay 0.76 over baseline, so the exact diff fails before the fix.</summary>
    [Fact]
    public void RuleBonus_MatchesRebalancedFormula()
    {
        var without = PromotionContentEvidence.Evaluate(Neutral, ProvenanceArchetype.WorkNote);
        var with = PromotionContentEvidence.Evaluate(
            Neutral with { RuleDensity = 2.0 }, ProvenanceArchetype.WorkNote);

        without.Adjustment.ShouldBeGreaterThan(-1.0, "headroom: baseline must sit off the floor");
        with.Adjustment.ShouldBeLessThan(1.0, "headroom: the delta must not be clamp-masked");
        with.Adjustment.ShouldBe(without.Adjustment + 0.60, 0.0001);
    }

    /// <summary>Caps move with the formula: the default channel caps at 0.70 (diff 0.95 over the
    /// -0.25 floor) and the plan channel stays lower at 0.35 (diff 0.60). Red direction: the
    /// old caps (1.00 / 0.45) pay diffs of 1.20 / 0.65, so both assertions fail before the fix.</summary>
    [Fact]
    public void RuleBonus_CapsAtSeventyHundredths_DefaultAndThirtyFive_Plan()
    {
        var workNoteBase = PromotionContentEvidence.Evaluate(Neutral, ProvenanceArchetype.WorkNote);
        var workNoteCapped = PromotionContentEvidence.Evaluate(
            Neutral with { RuleDensity = 5.0 }, ProvenanceArchetype.WorkNote);
        var planBase = PromotionContentEvidence.Evaluate(Neutral, ProvenanceArchetype.Plan);
        var planCapped = PromotionContentEvidence.Evaluate(
            Neutral with { RuleDensity = 5.0 }, ProvenanceArchetype.Plan);

        workNoteCapped.Adjustment.ShouldBeLessThan(1.2, "headroom: the cap must bind before the clamp");
        planCapped.Adjustment.ShouldBeLessThan(1.0, "headroom: the cap must bind before the clamp");
        workNoteCapped.Adjustment.ShouldBe(workNoteBase.Adjustment + 0.95, 0.0001);
        planCapped.Adjustment.ShouldBe(planBase.Adjustment + 0.60, 0.0001);
    }

    /// <summary>A two-item checklist is already checklist-shaped: it tags and costs 0.45.
    /// Red direction: the old trip needs 3+ items, so a two-item chunk stays silent and the
    /// exact penalty fails before the fix.</summary>
    [Fact]
    public void TwoItemChecklist_TagsAndPenalizesFortyFiveHundredths()
    {
        var without = EvaluateFeatures(Neutral);
        var with = EvaluateFeatures(Neutral with { Imperatives = 2 });

        with.Adjustment.ShouldBeGreaterThan(-1.50, "headroom: the penalty must not be floor-masked");
        with.Reasons.ShouldContain("imperative-checklist");
        with.Adjustment.ShouldBe(without.Adjustment - 0.45, 0.0001);
    }

    /// <summary>Boundary guard: a single imperative line is prose with a verb, not a checklist.</summary>
    [Fact]
    public void OneItemChecklist_StaysSilent()
    {
        var one = EvaluateFeatures(Neutral with { Imperatives = 1 });

        one.Reasons.ShouldNotContain("imperative-checklist");
    }

    /// <summary>Combined effect, off-clamp: rule phrasing plus checklist shape on one chunk —
    /// the counterweight bites the full 0.45 off the rule-only score. Red direction: with the
    /// old trip the two-item chunk pays nothing, so the exact bite fails before the fix.</summary>
    [Fact]
    public void RulePlusChecklist_CounterweightBites()
    {
        var ruleOnly = EvaluateFeatures(Neutral with { RuleDensity = 2.0 });
        var combined = EvaluateFeatures(Neutral with { RuleDensity = 2.0, Imperatives = 2 });

        ruleOnly.Adjustment.ShouldBeLessThan(1.0, "headroom: the rule bonus must not be clamp-masked");
        combined.Adjustment.ShouldBeGreaterThan(-1.50, "headroom: the net must sit off the floor");
        combined.Reasons.ShouldContain("rule-language");
        combined.Reasons.ShouldContain("imperative-checklist");
        combined.Adjustment.ShouldBe(ruleOnly.Adjustment - 0.45, 0.0001);
    }

    /// <summary>The verified-contract gate loosens to 0.6: rule phrasing at 0.6-0.79 density
    /// with a measurement behind it already fires, while below 0.6 it stays silent.
    /// Red direction: the old gate needs 0.8, so the in-band assertion fails before the fix.</summary>
    [Fact]
    public void VerifiedContract_FiresAtSixTenthsDensity_SilentBelow()
    {
        var firing = EvaluateFeatures(Neutral with { RuleDensity = 0.7, MeasureWords = 1 });
        var silent = EvaluateFeatures(Neutral with { RuleDensity = 0.5, MeasureWords = 1 });

        firing.Adjustment.ShouldBeLessThan(1.0, "headroom: the bonus must not be clamp-masked");
        firing.Reasons.ShouldContain("verified-contract");
        silent.Reasons.ShouldNotContain("verified-contract");
    }

    /// <summary>Exact diff of the verified-contract bonus: toggling the measure word (with no
    /// number+unit, so measured-values pays nothing) moves the score by exactly 0.50.
    /// Red direction: the old bonus pays 0.35, so the exact diff fails before the fix.</summary>
    [Fact]
    public void VerifiedContract_AddsExactlyFiftyHundredths()
    {
        var withoutMeasure = EvaluateFeatures(Neutral with { RuleDensity = 1.0 });
        var withMeasure = EvaluateFeatures(Neutral with { RuleDensity = 1.0, MeasureWords = 1 });

        withoutMeasure.Adjustment.ShouldBeGreaterThan(-1.0, "headroom: baseline must sit off the floor");
        withMeasure.Adjustment.ShouldBeLessThan(1.0, "headroom: the bonus must not be clamp-masked");
        withMeasure.Reasons.ShouldContain("verified-contract");
        withMeasure.Adjustment.ShouldBe(withoutMeasure.Adjustment + 0.50, 0.0001);
    }
}
