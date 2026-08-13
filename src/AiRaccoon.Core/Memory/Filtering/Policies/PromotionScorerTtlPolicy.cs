using System;
namespace AiRaccoon.Core.Memory.Filtering.Policies;

public sealed class PromotionScorerTtlPolicy : IAutoTtlPolicy
{
    public string Name => "PromotionScorerHeuristic";
    private const double NoiseThreshold = 0.6;
    private const int TransientTtlDays = 3;

    public int? EvaluateTtl(MemoryWriteRequest request)
    {
        var dummyRow = new ExtractionCandidateRow(
            Hash: string.Empty,
            Path: request.SourceFile ?? "memory_write",
            Value: request.Content,
            SourceFile: request.SourceFile,
            Rating: 0,
            AccessCount: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            TtlDays: null,
            SourceType: null
        );
        var (score, _) = PromotionScorer.Score(dummyRow, request.ProjectId, Array.Empty<string>());
        if (score < NoiseThreshold)
        {
            return TransientTtlDays;
        }
        return null;
    }
}
