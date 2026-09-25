namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Where a local embedding session runs; the <c>embedding.device</c> setting (ADR-0108).</summary>
public enum EmbeddingDevice
{
    /// <summary>The bundled engine on the GPU, other models on the CPU (the default).</summary>
    Auto,

    /// <summary>Every local model on the GPU where the platform has one.</summary>
    Gpu,

    /// <summary>Every local model on the CPU.</summary>
    Cpu,

    /// <summary>The bundled engine only, through the onnxruntime MLX plugin execution provider (ADR-0110).</summary>
    Mlx,

    /// <summary>Every local model through the onnxruntime CUDA plugin execution provider, from a user-supplied library.</summary>
    Cuda,

    /// <summary>The bundled engine only, through the CoreML execution provider on the Neural Engine (osx-arm64, ADR-0118).</summary>
    CoreMl
}

/// <summary>Parses <c>embedding.device</c> and decides whether a session tries the GPU, MLX or CoreML.</summary>
public static class EmbeddingDeviceSetting
{
    /// <summary>The setting's accepted values, as the CLI and doctor spell them.</summary>
    public static readonly string[] Values = ["auto", "gpu", "cpu", "mlx", "cuda", "coreml"];

    /// <summary>The stored value as a device; unset or unrecognized is <see cref="EmbeddingDevice.Auto" />.</summary>
    public static EmbeddingDevice Parse(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "gpu" => EmbeddingDevice.Gpu,
            "cpu" => EmbeddingDevice.Cpu,
            "mlx" => EmbeddingDevice.Mlx,
            "cuda" => EmbeddingDevice.Cuda,
            "coreml" => EmbeddingDevice.CoreMl,
            _ => EmbeddingDevice.Auto
        };

    /// <summary>
    ///     Auto trusts the GPU only for the bundled fp16 engine, whose GPU vectors match its CPU ones
    ///     (cosine 0.9998; a quantized user model can drift, cosine 0.94, so that opts in via `gpu`).
    ///     <see cref="EmbeddingDevice.Mlx" />, <see cref="EmbeddingDevice.Cuda" /> and
    ///     <see cref="EmbeddingDevice.CoreMl" /> keep this true too — CoreML falls back to WebGPU while
    ///     the Neural Engine compiles or is refused, and a custom model ignores it and gets the CPU.
    /// </summary>
    public static bool PrefersGpu(EmbeddingDevice device, bool isBundledEngine) =>
        device switch
        {
            EmbeddingDevice.Gpu => true,
            EmbeddingDevice.Cpu => false,
            EmbeddingDevice.Mlx => isBundledEngine,
            EmbeddingDevice.CoreMl => isBundledEngine,
            EmbeddingDevice.Cuda => true,
            _ => isBundledEngine
        };

    /// <summary>
    ///     MLX is opt-in and proven only against the bundled engine's rewritten graph (ADR-0110) — a
    ///     custom model has no such graph, so <c>device mlx</c> is inert for it rather than guessing.
    ///     The platform, plugin-file and graph-file checks that gate the actual attempt are the
    ///     caller's concern; this is only the setting's own decision.
    /// </summary>
    public static bool PrefersMlx(EmbeddingDevice device, bool isBundledEngine) =>
        device == EmbeddingDevice.Mlx && isBundledEngine;

    /// <summary>
    ///     CoreML is opt-in and only wired to the bundled engine's ANE-layout export (ADR-0118) — a
    ///     custom model has no such graph, so <c>device coreml</c> is inert for it rather than guessing.
    ///     The platform, plugin and graph-file checks that gate the actual attempt are the caller's
    ///     concern; this is only the setting's own decision.
    /// </summary>
    public static bool PrefersCoreMl(EmbeddingDevice device, bool isBundledEngine) =>
        device == EmbeddingDevice.CoreMl && isBundledEngine;

    /// <summary>
    ///     The CUDA provider library a session should try: the stored path under device cuda ("" when unset,
    ///     which the session refuses with a hint), otherwise null — a path left over from cuda stays inert.
    ///     Cuda applies to every local model, so a failed attempt falls back to WebGPU for each of them.
    /// </summary>
    public static string? CudaLibraryFor(EmbeddingDevice device, string? stored) =>
        device == EmbeddingDevice.Cuda ? stored ?? "" : null;
}
