namespace AiRaccoon.Core.Memory;

/// <summary>Combines the provenance-channel prior, bounded content evidence and the organic refinement
/// layer into a single 0-4 score with plain-name reason tags (ported from agentC/scorer.py's
/// score_candidate(), see docs/adr/0018-promotion-scoring-v2.md v3 section).</summary>
internal static class PromotionScorer
{
    private const int MinWordsFloor = 8;
    private const double MinWordsCap = 0.50;
    private const int ShortChunkWords = 25;
    private const double ShortChunkCap = 0.60;
    private const double AutoMemoryIndexRuleLift = 0.15;
    private const double AutoMemoryIndexRuleLiftThreshold = 1.0;

    /// <summary>Channels the payload describes other work or other documents: content cannot buy them
    /// back very far.</summary>
    private static readonly HashSet<ProvenanceArchetype> HardNoiseChannels =
    [
        ProvenanceArchetype.TurnMirror,
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
