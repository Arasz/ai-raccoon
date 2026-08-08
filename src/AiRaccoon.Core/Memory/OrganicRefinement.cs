namespace AiRaccoon.Core.Memory;

/// <summary>Refined score plus the plain-name reason tags the organic refinement layer fired.</summary>
internal readonly record struct OrganicRefinementResult(double Score, IReadOnlyList<string> Reasons);

/// <summary>Refines the channel prior for organic-note candidates: without it the model cannot tell a
/// status/turn-mirror dump from a durable fact. Ported from agentC/scorer.py's organic_adjust() and the
/// organic_note branch of score_candidate() (see docs/adr/0018-promotion-scoring-v2.md v3 section).
/// Only called for the organic-note channel; routing itself lives in ProvenanceArchetypeClassifier.</summary>
internal static class OrganicRefinement
{
    private const double DeltaLo = -2.2;
    private const double DeltaHi = 1.6;
    private const double ShortDefinitionalFloor = 2.4;
    private const int ShortDefinitionalMaxWords = 45;
    private const int VeryShortMaxWords = 15;

    internal static OrganicRefinementResult Apply(
        double baseScore, string value, string projectId, IReadOnlyList<string> allProjectIds) =>
        Apply(CandidateFeatureExtractor.Extract(value ?? string.Empty, projectId, allProjectIds), baseScore);

    internal static OrganicRefinementResult Apply(CandidateFeatures f, double baseScore)
    {
        var reasons = new List<string>();
        var delta = 0.0;

        if (f.StatusOpener)
        {
            delta -= 1.20;
            reasons.Add("status-opener");
        }

        if (f.StatusVocab > 0)
        {
            delta -= Math.Min(1.5, 0.15 * f.StatusVocab);
            reasons.Add("status-vocabulary");
        }

        if (f.SecondPerson)
        {
            delta -= 0.50;
            reasons.Add("second-person");
        }

        if (f.CommitHashes >= 2)
        {
            delta -= 0.30;
            reasons.Add("commit-hashes");
        }

        if (f.RealMeasures >= 2)
        {
            delta += 0.50;
            reasons.Add("real-measurements");
        }

        if (f.DurableLoose > 0)
        {
            delta += Math.Min(1.0, 0.35 * f.DurableLoose);
            reasons.Add("durable-fact-language");
        }

        if (f.DatedFact)
        {
            delta += 0.50;
            reasons.Add("dated-fact");
        }

        if (f.ForeignSubject)
        {
            delta += 0.20;
            reasons.Add("foreign-subject");
        }

        // Pointer-shaped organic writes are still pointers, even in the organic-note channel.
        if (f.LinkDensity >= 1.5 || f.DocnameDensity >= 2.5)
        {
            delta -= 0.40;
            reasons.Add("pointer-density");
        }

        if (f.TableFrac >= 0.55)
        {
            delta -= 0.50;
            reasons.Add("table-shaped");
        }

        if (f.ContentsIndex)
        {
            delta -= 1.20;
            reasons.Add("contents-index");
        }

        if (f.Urls >= 3 && f.DurableLoose <= 2)
        {
            delta -= 0.50;
            reasons.Add("link-heavy");
        }

        if (f.DocnameDensity >= 4.0 && f.DurableLoose == 0 && f.RuleDensity < 0.5)
        {
            delta -= 0.30;
            reasons.Add("docname-heavy");
        }

        if (f.MetaHeader >= 1)
        {
            delta -= 0.35;
            reasons.Add("metadata-header");
        }

        if (f.Imperatives >= 3)
        {
            delta -= 0.40;
            reasons.Add("imperative-checklist");
        }

        if (f.DirReadme)
        {
            delta -= 0.80;
            reasons.Add("directory-readme");
        }

        if (f.FindingRows >= 3)
        {
            delta -= 0.70;
            reasons.Add("finding-rows");
        }

        if (f.Superseded)
        {
            delta -= 0.40;
            reasons.Add("superseded");
        }

        // One-breath durable rule: a short definitional fact keeps a floor so it survives.
        if (f.NWords < ShortDefinitionalMaxWords && f.DurableLoose >= 1 && f.StatusVocab <= 1 && !f.StatusOpener)
        {
            baseScore = Math.Max(baseScore, ShortDefinitionalFloor);
            reasons.Add("short-definitional-floor");
        }

        if (f.NWords < VeryShortMaxWords)
        {
            delta -= 0.30;
            reasons.Add("very-short-penalty");
        }

        delta = Math.Clamp(delta, DeltaLo, DeltaHi);
        var score = Math.Clamp(baseScore + delta, 0.0, 4.0);
        return new OrganicRefinementResult(score, reasons);
    }
}
