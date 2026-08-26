namespace AiRaccoon.Core.Memory;

/// <summary>
///     The memory embedding engine's setup command and its no-API-key remedy. Every surface that
///     can notice the memory engine is missing or unkeyed — `doctor`, the availability warnings,
///     the runtime failure path — quotes these verbatim rather than spelling a command of its own.
/// </summary>
public static class EmbeddingEngineSetup
{
    /// <summary>Installs the bundled default local engine (`model embedding set local`, ADR-0076); the memory twin of CodeEngineSetup.DefaultModelCommand.</summary>
    public const string DefaultModelCommand = "ai-raccoon model embedding set local";

    /// <summary>The em-dash clause `doctor` appends when a remote provider has no API key — verbatim from SettingsCommands.ModelSetOpenAiAsync's warning.</summary>
    public const string NoApiKeyRemedy =
        "no API key set; run 'ai-raccoon model embedding set openai <model> --api-key <key>' or embeddings will fail";
}
