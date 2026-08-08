namespace AiRaccoon.Core.Memory;

/// <summary>Combines the provenance-archetype prior, bounded content evidence and the organic
/// refinement layer into a single 0-4 score with plain-name reason tags, in that order (see
/// docs/adr/0018-promotion-scoring-v2.md).</summary>
internal static class PromotionScorer
{
    private const int ShortChunkWords = 25;
    private const double ShortChunkCap = 0.60;

    internal static (double Score, IReadOnlyList<string> Reasons) Score(
        ExtractionCandidateRow row, string projectId, IReadOnlyList<string> allProjectIds)
    {
        var archetype = ProvenanceArchetypeClassifier.Classify(row.Path, row.SourceFile, row.Value);
        var evidence = PromotionContentEvidence.Evaluate(
            row.Value, archetype, projectId, allProjectIds, row.AccessCount);

        var reasons = new List<string> { ProvenanceArchetypeClassifier.Tag(archetype) };
        reasons.AddRange(evidence.Reasons);

        var baseScore = ProvenanceArchetypeClassifier.Prior(archetype) + evidence.Adjustment;

        // No provenance rescues an entry with nothing in it.
        if (PromotionContentEvidence.WordCount(row.Value) < ShortChunkWords && baseScore > ShortChunkCap)
        {
            baseScore = ShortChunkCap;
            reasons.Add("thin-cap");
        }

        baseScore = Math.Clamp(baseScore, 0.0, 4.0);

        var refined = OrganicRefinement.Apply(baseScore, row.SourceFile, row.Value);
        reasons.AddRange(refined.Reasons);

        return (refined.Score, reasons);
    }
}
