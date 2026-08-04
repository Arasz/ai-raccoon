namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Settings-table keys for the embedding engine (FR-NM-3).</summary>
public static class EmbeddingSettingsKeys
{
    public const string Provider = "embedding.provider";
    public const string Model = "embedding.model";
    public const string BaseUrl = "embedding.baseUrl";

    /// <summary>Engine fingerprint; memory_configure re-embeds when it changes.</summary>
    public const string Engine = "embedding.engine";

    /// <summary>OpenAI API key, persisted in the settings table (single-channel ruling 2026-08-04).</summary>
    public const string ApiKey = "embedding.apiKey";
}