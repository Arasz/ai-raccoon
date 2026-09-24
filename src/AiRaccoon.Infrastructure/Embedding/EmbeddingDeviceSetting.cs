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
    Mlx
}

/// <summary>Parses <c>embedding.device</c> and decides whether a session tries the GPU or MLX.</summary>
public static class EmbeddingDeviceSetting
{
    /// <summary>The setting's accepted values, as the CLI and doctor spell them.</summary>
    public static readonly string[] Values = ["auto", "gpu", "cpu", "mlx"];

    /// <summary>The stored value as a device; unset or unrecognized is <see cref="EmbeddingDevice.Auto" />.</summary>
    public static EmbeddingDevice Parse(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "gpu" => EmbeddingDevice.Gpu,
            "cpu" => EmbeddingDevice.Cpu,
            "mlx" => EmbeddingDevice.Mlx,
            _ => EmbeddingDevice.Auto
        };

    /// <summary>
    ///     Auto trusts the GPU only for the bundled fp16 engine, whose GPU vectors match its CPU ones
    ///     (cosine 0.9998); a quantized user model can drift on the GPU (cosine 0.94), so it opts in.
    ///     <see cref="EmbeddingDevice.Mlx" /> keeps this true for the bundled engine too, so a failed
    ///     MLX attempt still falls back to the GPU before the CPU.
    /// </summary>
    public static bool PrefersGpu(EmbeddingDevice device, bool isBundledEngine) =>
        device switch
        {
            EmbeddingDevice.Gpu => true,
            EmbeddingDevice.Cpu => false,
            EmbeddingDevice.Mlx => isBundledEngine,
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
}
