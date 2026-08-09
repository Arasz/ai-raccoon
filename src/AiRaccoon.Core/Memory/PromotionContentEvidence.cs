namespace AiRaccoon.Core.Memory;

/// <summary>Bounded content-shape adjustment plus the plain-name reason tags that fired.</summary>
internal readonly record struct ContentEvidence(double Adjustment, IReadOnlyList<string> Reasons);

/// <summary>Bounded content-shape evidence that moves a doc-channel candidate off its channel prior, plus
/// the bespoke auto-memory-note evidence (ported from agentC/scorer.py's doc_adjust() and the
/// auto_memory_note branch of score_candidate(), see docs/adr/0018-promotion-scoring-v2.md v3 section).</summary>
internal static class PromotionContentEvidence
{
    private const double Lo = -1.60;
    private const double Hi = 1.30;
    private const double PlanRuleCap = 0.45;
    private const double DefaultRuleCap = 1.10;

    private const double AutoMemoryNoteLo = -1.8;
    private const double AutoMemoryNoteHi = 1.2;
    private const double AutoMemoryNoteRuleCap = 0.60;

    internal static ContentEvidence Evaluate(
        string value, ProvenanceArchetype archetype, string projectId, IReadOnlyList<string> allProjectIds) =>
        Evaluate(CandidateFeatureExtractor.Extract(value ?? string.Empty, projectId, allProjectIds), archetype);

    internal static ContentEvidence Evaluate(CandidateFeatures f, ProvenanceArchetype archetype)
    {
        var reasons = new List<string>();
        var adj = 0.0;

        var ruleCap = archetype == ProvenanceArchetype.Plan ? PlanRuleCap : DefaultRuleCap;
        var ruleBonus = Clamp(0.38 * f.RuleDensity, 0.0, ruleCap);
        if (ruleBonus > 0)
        {
            adj += ruleBonus;
            reasons.Add("rule-language");
        }

        var measuredBonus = MeasuredBonus(f);
        if (measuredBonus > 0)
        {
            adj += measuredBonus;
            reasons.Add("measured-values");
        }

        if (f.ForeignSubject)
        {
            adj += 0.25;
            reasons.Add("foreign-subject");
        }

        if (f.ForeignProjects >= 2)
        {
            adj += 0.10;
            reasons.Add("many-foreign-projects");
        }

        if (f.HeadingStart)
        {
            adj += 0.10;
            reasons.Add("heading-start");
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

        if (f.Imperatives >= 3)
        {
            adj -= 0.30;
            reasons.Add("imperative-checklist");
        }

        // A durable rule with a verified measurement behind it is the strongest combination.
        if (f.MeasureWords >= 1 && f.RuleDensity >= 0.8)
        {
            adj += 0.35;
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

        var thin = false;
        if (f.NChars < 420)
        {
            adj -= 0.35;
            thin = true;
        }

        if (f.NWords < 60)
        {
            adj -= 0.25;
            thin = true;
        }

        if (thin)
        {
            reasons.Add("thin-content");
        }

        var clamped = Clamp(adj, Lo, Hi);
        // Applied after the clamp: a saturated-Hi chunk must still be demoted for opening
        // mid-sentence. Doc-channel only, by decision — wiring into OrganicNote/AutoMemoryNote
        // would be an unmeasured weight change; revisit only with calibration fixtures (ADR-0018).
        if (f.MidSentence)
        {
            clamped -= 0.18;
            reasons.Add("mid-sentence");
        }

        return new ContentEvidence(clamped, reasons);
    }

    /// <summary>Curated durable note; still checked for status shape (session dumps mis-filed as notes).</summary>
    internal static ContentEvidence EvaluateAutoMemoryNote(
        string value, string projectId, IReadOnlyList<string> allProjectIds) =>
        EvaluateAutoMemoryNote(CandidateFeatureExtractor.Extract(value ?? string.Empty, projectId, allProjectIds));

    internal static ContentEvidence EvaluateAutoMemoryNote(CandidateFeatures f)
    {
        var reasons = new List<string>();
        var adj = 0.0;

        var ruleBonus = Clamp(0.20 * f.RuleDensity, 0.0, AutoMemoryNoteRuleCap);
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

    private static double Clamp(double x, double lo, double hi) => x < lo ? lo : x > hi ? hi : x;
}
