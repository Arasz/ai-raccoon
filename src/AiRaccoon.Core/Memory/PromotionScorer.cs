namespace AiRaccoon.Core.Memory;

/// <summary>Combines the provenance-channel prior, bounded content evidence and the organic refinement
/// layer into a single 0-4 score with plain-name reason tags (ported from scorer.py's
/// score_candidate(), see docs/adr/0018-promotion-scoring-v2.md round-3 lane-A section).</summary>
internal static class PromotionScorer
{
    /// <summary>Stamped onto every queue row this scorer produces; bumped whenever the scoring
    /// model changes in a way that makes previously-computed scores incomparable
/// (docs/adr/0018-promotion-scoring-v2.md). Independent of the ADR's design-generation labels.
    /// 1 stamped the v3 model this mechanism shipped with (#246); 2 is the round-3 lane-A model.</summary>
    internal const int Version = 2;

    private const int MinWordsFloor = 8;
    private const double MinWordsCap = 0.50;
    private const int ShortChunkWords = 25;
    private const double ShortChunkCap = 0.60;
    private const double AutoMemoryIndexRuleLift = 0.15;
    private const double AutoMemoryIndexRuleLiftThreshold = 1.0;
    private const double DocIndexRuleGain = 0.25;
    private const double DocIndexLiftLo = -0.30;
    private const double DocIndexLiftHi = 0.60;

    /// <summary>Channels the payload describes other work or other documents: content cannot buy them
    /// back very far.</summary>
    private static readonly HashSet<ProvenanceArchetype> HardNoiseChannels =
    [
        ProvenanceArchetype.TurnMirror,
        ProvenanceArchetype.Transcript,
        ProvenanceArchetype.RememberLog,
        ProvenanceArchetype.AutoMemorySession,
        ProvenanceArchetype.AutoMemoryIndex,
        ProvenanceArchetype.DocIndex
    ];

    internal static (double Score, IReadOnlyList<string> Reasons) Score(
        ExtractionCandidateRow row, string projectId, IReadOnlyList<string> allProjectIds)
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(row.Path, row.SourceFile, row.Value);
        var (effectiveValue, _) = TurnMirrorPrefix.Split(row.Value ?? string.Empty);
        var features = CandidateFeatureExtractor.Extract(effectiveValue, projectId, allProjectIds);

        var reasons = new List<string> { ProvenanceArchetypeClassifier.Tag(archetype) };
        var prior = ProvenanceArchetypeClassifier.Prior(archetype);

        // No provenance rescues an entry with almost nothing in it, regardless of channel.
        if (features.NWords < MinWordsFloor)
        {
            reasons.Add("too-short");
            return (Math.Clamp(Math.Min(prior, MinWordsCap), 0.0, 4.0), reasons);
        }

        if (HardNoiseChannels.Contains(archetype))
        {
            var lift = 0.0;
            if (archetype == ProvenanceArchetype.AutoMemoryIndex && features.RuleDensity >= AutoMemoryIndexRuleLiftThreshold)
            {
                lift = AutoMemoryIndexRuleLift;
                reasons.Add("index-rule-lift");
            }

            if (archetype == ProvenanceArchetype.DocIndex)
            {
                lift = PromotionContentEvidence.Clamp(
                    DocIndexRuleGain * features.RuleDensity + PromotionContentEvidence.Substance(features),
                    DocIndexLiftLo, DocIndexLiftHi);
                reasons.Add("doc-index-lift");
            }

            return (Math.Clamp(prior + lift, 0.0, 4.0), reasons);
        }

        if (archetype == ProvenanceArchetype.OrganicNote)
        {
            var refined = OrganicRefinement.Apply(features, prior);
            reasons.AddRange(refined.Reasons);
            return (refined.Score, reasons);
        }

        if (archetype == ProvenanceArchetype.AutoMemoryNote)
        {
            var noteEvidence = PromotionContentEvidence.EvaluateAutoMemoryNote(features);
            reasons.AddRange(noteEvidence.Reasons);
            return (Math.Clamp(prior + noteEvidence.Adjustment, 0.0, 4.0), reasons);
        }

        var evidence = PromotionContentEvidence.Evaluate(features, archetype);
        reasons.AddRange(evidence.Reasons);
        var score = Math.Clamp(prior + evidence.Adjustment, 0.0, 4.0);
        if (features.NWords < ShortChunkWords)
        {
            score = Math.Min(score, ShortChunkCap);
            reasons.Add("thin-cap");
        }

        return (score, reasons);
    }
}
