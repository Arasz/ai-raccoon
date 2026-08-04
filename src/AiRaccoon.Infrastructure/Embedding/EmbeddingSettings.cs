namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Resolved embedding-engine settings; provider is local or openai, the rest is provider-specific.</summary>
public sealed record EmbeddingSettings(
    string Provider,
    string? Model,
    string? BaseUrl,
    string? ApiKey);
