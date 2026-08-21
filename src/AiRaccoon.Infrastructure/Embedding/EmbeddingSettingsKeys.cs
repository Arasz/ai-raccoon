namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Settings-table keys for the embedding engine (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature).
/// </summary>
public static class EmbeddingSettingsKeys
{
    public const string Provider = "embedding.provider";
    public const string Model = "embedding.model";
    public const string BaseUrl = "embedding.baseUrl";

    /// <summary>Engine fingerprint; `ai-raccoon model set` re-embeds when it changes.</summary>
    public const string Engine = "embedding.engine";

    /// <summary>OpenAI API key, persisted in the settings table (single-channel ruling 2026-08-04).</summary>
    public const string ApiKey = "embedding.apiKey";

    /// <summary>Remote engines declare their output dimension here — sqlite-vec infers none, so the
    /// drain needs it to reconcile vec0 before writing (D2).</summary>
    public const string Dimensions = "embedding.dimensions";
}
