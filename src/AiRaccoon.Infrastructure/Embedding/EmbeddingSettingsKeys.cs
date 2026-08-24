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

    /// <summary>Engine fingerprint; `ai-raccoon model embedding set` re-embeds when it changes.</summary>
    public const string Engine = "embedding.engine";

    /// <summary>OpenAI API key, persisted in the settings table (single-channel ruling 2026-08-04).</summary>
    public const string ApiKey = "embedding.apiKey";

    /// <summary>Remote engines declare their output dimension here — sqlite-vec infers none, so the
    /// drain needs it to reconcile vec0 before writing (D2).</summary>
    public const string Dimensions = "embedding.dimensions";

    /// <summary>The manifest-activated local code engine's directory (§3.3 D-E9); `ai-raccoon model code set local`
    /// writes this. Independent of <see cref="Model" /> — the code corpus has its own engine.</summary>
    public const string CodeModel = "embedding.codeModel";

    /// <summary>Code engine fingerprint; a change invalidates the code corpus's embedded rows to 'pending'.</summary>
    public const string CodeEngine = "embedding.codeEngine";

    /// <summary>The code engine's output dimension; written at activation, read at server open.</summary>
    public const string CodeDimensions = "embedding.codeDimensions";

    /// <summary>ORT intra-op thread cap (WP11-A/G16): unset halves the physical core count (min 1),
    /// "0" is ORT's own default. Takes effect on the next server restart — sessions are cached per fingerprint.</summary>
    public const string Threads = "embedding.threads";
}
