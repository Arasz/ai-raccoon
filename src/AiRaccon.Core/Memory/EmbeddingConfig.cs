namespace AiRaccon.Core.Memory;

/// <summary>Embedding provider/model configuration for the bank (spec §4.1 memory_configure).</summary>
public sealed record EmbeddingConfig(string Provider, string Model, string Engine);
