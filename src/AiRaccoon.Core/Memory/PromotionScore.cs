namespace AiRaccoon.Core.Memory;

/// <summary>A candidate's promotion score (0-4, docs/adr/0018-promotion-scoring-v2.md) and the plain-name reason tags that produced it.</summary>
internal readonly record struct PromotionScore(double Score, IReadOnlyList<string> Reasons);
