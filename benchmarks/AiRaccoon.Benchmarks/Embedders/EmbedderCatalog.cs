namespace AiRaccoon.Benchmarks.Embedders;

/// <summary>
///     The embedding backends a benchmark run can compare. LM Studio model ids come from the
///     LMSTUDIO_MODELS environment variable (comma-separated; default the two models verified
///     on the dev box); the local backend needs AIRACCOON_TEST_GGUF pointing at a GGUF file.
///     ONNX models include the bundled int8 model (from src/AiRaccoon/Models/) and any manifest
///     models under ~/.ai-raccoon/models/.
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

        if (name.StartsWith("onnx:bundled:", StringComparison.Ordinal))
        {
            var parts = name["onnx:bundled:".Length..].Split('|');
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new ArgumentException(
                    $"Invalid bundled ONNX embedder name '{name}'. Expected 'onnx:bundled:<modelPath>|<vocabPath>'.",
                    nameof(name));
            }

            return new OnnxModelEmbedder(parts[0], parts[1]);
        }

        if (name.StartsWith("onnx:manifest:", StringComparison.Ordinal))
        {
            var dir = name["onnx:manifest:".Length..];
            return new OnnxModelEmbedder(dir);
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

        // Bundled ONNX model from the repo's src/AiRaccoon/Models/
        var repoRoot = FindRepoRoot();
        var modelsSubdir = repoRoot is null ? null : Path.Combine(repoRoot, "src", "AiRaccoon", "Models");
        var onnxPath = modelsSubdir is null ? null : Path.Combine(modelsSubdir, "model_qint8_arm64.onnx");
        var vocabPath = modelsSubdir is null ? null : Path.Combine(modelsSubdir, "vocab.txt");
        if (onnxPath is not null && vocabPath is not null && File.Exists(onnxPath) && File.Exists(vocabPath))
        {
            names.Add($"onnx:bundled:{onnxPath}|{vocabPath}");
        }
        else
        {
            Console.Error.WriteLine(
                $"embedder discovery: bundled ONNX model not found (repo root: {repoRoot ?? "<unknown>"}); skipping.");
        }

        // Manifest-based ONNX models from ~/.ai-raccoon/models/
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var modelsDir = Path.Combine(home, ".ai-raccoon", "models");
        var manifests = Directory.Exists(modelsDir)
            ? Directory.GetDirectories(modelsDir).Where(d => File.Exists(Path.Combine(d, "ai-raccoon.manifest.json"))).ToList()
            : [];
        if (manifests.Count == 0)
        {
            Console.Error.WriteLine($"embedder discovery: no manifest ONNX models under {modelsDir}; skipping.");
        }

        names.AddRange(manifests.Select(d => $"onnx:manifest:{d}"));

        return names;
    }

    /// <summary>Walks up from the current directory to find the Git repo root.</summary>
    private static string? FindRepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
