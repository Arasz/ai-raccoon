namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
/// The embedding backends a benchmark run can compare. LM Studio model ids come from the
/// LMSTUDIO_MODELS environment variable (comma-separated; default the two models verified
/// on the dev box); the local backend needs AIRACCOON_TEST_GGUF pointing at a GGUF file.
/// </summary>
public static class EmbedderCatalog
{
    public static IReadOnlyList<string> Names { get; } = BuildNames();

    public static IEmbedder Create(string name)
    {
        if (name.StartsWith("local:", StringComparison.Ordinal))
        {
            return new LocalGgufEmbedder();
        }

        if (name.StartsWith("lmstudio:", StringComparison.Ordinal))
        {
            var model = name["lmstudio:".Length..];
            return new LmStudioEmbedder(model);
        }

        throw new ArgumentException($"Unknown embedder '{name}'.", nameof(name));
    }

    private static IReadOnlyList<string> BuildNames()
    {
        var names = new List<string> { "local:all-MiniLM-L6-v2.Q5_K_M.gguf" };

        var env = Environment.GetEnvironmentVariable("LMSTUDIO_MODELS");
        var models = string.IsNullOrWhiteSpace(env)
            ? ["text-embedding-qwen3-embedding-0.6b", "text-embedding-embeddinggemma-300m"]
            : env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        names.AddRange(models.Select(m => $"lmstudio:{m}"));

        return names;
    }
}
