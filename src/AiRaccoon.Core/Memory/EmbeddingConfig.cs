namespace AiRaccoon.Core.Memory;

/// <summary>Embedding provider/model configuration for the bank (written by `ai-raccoon model embedding set`).</summary>
public sealed record EmbeddingConfig(string Provider, string Model, string Engine);
