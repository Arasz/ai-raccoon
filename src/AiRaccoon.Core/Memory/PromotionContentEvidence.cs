namespace AiRaccoon.Core.Memory;

/// <summary>Bounded content-shape adjustment plus the plain-name reason tags that fired.</summary>
internal readonly record struct ContentEvidence(double Adjustment, IReadOnlyList<string> Reasons);

/// <summary>
///     Bounded content-shape evidence that moves a doc-channel candidate off its channel prior, plus
///     the bespoke auto-memory-note evidence and the three centred terms (substance, durability, portability)
///     shared with <see cref="OrganicRefinement" /> (ported from scorer.py's doc_adjust(), organic_adjust()'s
///     shared helpers, and the auto_memory_note branch of score_candidate(), see
///     docs/adr/0018-promotion-scoring-v2.md round-3 lane-A section).
/// </summary>
internal static class PromotionContentEvidence
{
    private const double Lo = -1.60;
    private const double Hi = 1.60;
    private const double PlanRuleCap = 0.35;
    private const double DefaultRuleCap = 0.70;
    private const double RuleGain = 0.30;
    private const double RuleCentre = 0.25;

    private const double AutoMemoryNoteLo = -1.8;
    private const double AutoMemoryNoteHi = 1.4;
    private const double AutoMemoryNoteRuleGain = 0.20;
    private const double AutoMemoryNoteRuleCap = 0.60;

    // Substance ramp: chunk length is the single strongest content feature on the training set. A
    // chunk near the corpus median length (119 words) scores 0; fragments are pushed down,
    // full-bodied chunks up. Replaces the old two cliff penalties at 420 chars / 60 words.
    private const double SubstancePivot = 110.0;
    private const double SubstanceSpan = 90.0;
    private const double SubstanceGain = 0.55;

    // Durability: an impersonal statement of what always/never holds is the shape a portable rule
    // takes. Centred so the median chunk contributes nothing.
    private const double ImpRuleGain = 0.30;
    private const double ImpRuleCap = 0.40;
    private const double DurabilityCentre = 0.08;

    // Portability: breadth of outside-world technology, less the density of inside-repo
    // bookkeeping. Centred on the typical document chunk (~2 distinct technologies named), so it
    // lifts and demotes in equal measure. Applied to considered technical documents only.
    private const double TechGain = 0.28;
    private const int TechCap = 5;
    private const double TechCentre = 0.55;
    private const double XrefGain = 0.15;
    private const double XrefCap = 0.45;

    internal static ContentEvidence Evaluate(
        string value, ProvenanceArchetype archetype, string projectId, IReadOnlyList<string> allProjectIds) =>
        Evaluate(CandidateFeatureExtractor.Extract(value ?? string.Empty, projectId, allProjectIds), archetype);

    internal static ContentEvidence Evaluate(CandidateFeatures f, ProvenanceArchetype archetype)
    {
        var reasons = new List<string>();
        var adj = 0.0;

        var ruleCap = archetype == ProvenanceArchetype.Plan ? PlanRuleCap : DefaultRuleCap;
        var ruleBonus = Clamp(RuleGain * f.RuleDensity - RuleCentre, -RuleCentre, ruleCap);
        adj += ruleBonus;
        if (f.RuleDensity >= 0.5)
        {
            reasons.Add("rule-language");
        }

        var measuredBonus = MeasuredBonus(f);
        if (measuredBonus > 0)
        {
            adj += measuredBonus;
            reasons.Add("measured-values");
        }

        if (ProvenanceArchetypeClassifier.DocFamily.Contains(archetype))
        {
            adj += Portability(f);
            reasons.Add("portability");
        }

        adj += Durability(f);
        if (f.ImpRuleDensity > 0)
        {
            reasons.Add("durable-rule-language");
        }

        adj += Substance(f);

        if (f.HeadingStart)
        {
            adj += 0.10;
            reasons.Add("heading-start");
        }

        if (f.ForeignSubject)
        {
            adj += 0.15;
            reasons.Add("foreign-subject");
        }

        if (f.MidSentence)
        {
            adj += 0.15;
            reasons.Add("mid-sentence");
        }

        var pointer = PointerPenalty(f);
        if (pointer > 0)
        {
            adj -= pointer;
            reasons.Add("pointer-density");
        }

        if (f.FindingRows >= 3)
        {
            adj -= 0.20;
            reasons.Add("finding-rows");
        }
        else if (f.FindingRows >= 1)
        {
            adj -= 0.12;
            reasons.Add("finding-rows");
        }

        var ephemeraPenalty = Clamp(0.22 * f.Ephemera, 0.0, 0.65);
        if (ephemeraPenalty > 0)
        {
            adj -= ephemeraPenalty;
            reasons.Add("ephemera");
        }

        var firstPersonPenalty = Clamp(0.18 * f.FirstPerson, 0.0, 0.55);
        if (firstPersonPenalty > 0)
        {
            adj -= firstPersonPenalty;
            reasons.Add("first-person");
        }

        if (f.MetaHeader >= 4)
        {
            adj -= 0.75;
            reasons.Add("metadata-header");
        }
        else if (f.MetaHeader >= 2)
        {
            adj -= 0.45;
            reasons.Add("metadata-header");
        }

        if (f.Imperatives >= 2)
        {
            adj -= 0.45;
            reasons.Add("imperative-checklist");
        }

        // A durable rule with a verified measurement behind it is the strongest combination.
        if (f.MeasureWords >= 1 && f.RuleDensity >= 0.6)
        {
            adj += 0.50;
            reasons.Add("verified-contract");
        }

        if (f.Superseded)
        {
            adj -= 0.40;
            reasons.Add("superseded");
        }

        if (f.Frontmatter && f.NChars < 900)
        {
            adj -= 0.55;
            reasons.Add("frontmatter-only");
        }
        else if (f.Frontmatter)
        {
            adj -= 0.25;
            reasons.Add("frontmatter-only");
        }

        return new ContentEvidence(Clamp(adj, Lo, Hi), reasons);
    }

    /// <summary>Curated durable note; still checked for status shape (session dumps mis-filed as notes).</summary>
    internal static ContentEvidence EvaluateAutoMemoryNote(
        string value, string projectId, IReadOnlyList<string> allProjectIds) =>
        EvaluateAutoMemoryNote(CandidateFeatureExtractor.Extract(value ?? string.Empty, projectId, allProjectIds));

    internal static ContentEvidence EvaluateAutoMemoryNote(CandidateFeatures f)
    {
        var reasons = new List<string>();
        var adj = 0.0;

        var ruleBonus = Clamp(AutoMemoryNoteRuleGain * f.RuleDensity, 0.0, AutoMemoryNoteRuleCap);
        if (ruleBonus > 0)
        {
            adj += ruleBonus;
            reasons.Add("rule-language");
        }

        if (f.MeasureWords >= 1 && f.NumUnit >= 1)
        {
            adj += 0.30;
            reasons.Add("measured-values");
        }

        if (f.ForeignSubject)
        {
            adj += 0.25;
            reasons.Add("foreign-subject");
        }

        adj += Substance(f);
        adj += Durability(f);
        if (f.ImpRuleDensity > 0)
        {
            reasons.Add("durable-rule-language");
        }

        var statusPenalty = Math.Min(1.2, 0.12 * f.StatusVocab);
        if (statusPenalty > 0)
        {
            adj -= statusPenalty;
            reasons.Add("status-vocabulary");
        }

        if (f.StatusOpener)
        {
            adj -= 0.80;
            reasons.Add("status-opener");
        }

        if (f.Superseded)
        {
            adj -= 0.40;
            reasons.Add("superseded");
        }

        return new ContentEvidence(Clamp(adj, AutoMemoryNoteLo, AutoMemoryNoteHi), reasons);
    }

    /// <summary>
    ///     Chunk-length ramp shared by every non-hard-noise branch (ported from scorer.py's
    ///     substance()).
    /// </summary>
    internal static double Substance(CandidateFeatures f) => SubstanceGain * Clamp((f.NWords - SubstancePivot) / SubstanceSpan, -1.0, 1.0);

    /// <summary>
    ///     Impersonal-rule-language term shared by every non-hard-noise branch (ported from
    ///     scorer.py's durability()).
    /// </summary>
    internal static double Durability(CandidateFeatures f) => Clamp(ImpRuleGain * f.ImpRuleDensity, 0.0, ImpRuleCap) - DurabilityCentre;

    /// <summary>
    ///     Breadth of outside-world technology minus density of inside-repo bookkeeping;
    ///     applied to considered technical documents only (ported from scorer.py's portability()).
    /// </summary>
    private static double Portability(CandidateFeatures f)
    {
        var lift = TechGain * Math.Min(f.TechBreadth, TechCap) - TechCentre;
        var drag = Clamp(XrefGain * f.XrefDensity, 0.0, XrefCap);
        return lift - drag;
    }

    private static double MeasuredBonus(CandidateFeatures f)
    {
        if (f.MeasureWords >= 1 && f.NumUnit >= 1)
        {
            return Clamp(0.12 * Math.Min(f.MeasureWords, 4) + 0.05 * Math.Min(f.NumUnit, 4), 0.0, 0.50);
        }

        return f.MeasureWords >= 2 ? 0.15 : 0.0;
    }

    private static double PointerPenalty(CandidateFeatures f)
    {
        var pointer = 0.0;
        if (f.TableFrac >= 0.55)
        {
            pointer += 0.30;
        }

        if (f.LinkDensity >= 1.5)
        {
            pointer += 0.45;
        }

        if (f.DocnameDensity >= 2.0)
        {
            pointer += 0.35;
        }

        if (f.VersionRows >= 3)
        {
            pointer += 0.35;
        }

        return Clamp(pointer, 0.0, 1.00);
    }

    internal static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;
}
