namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>Where a local embedding session runs; the <c>embedding.device</c> setting (ADR-0108).</summary>
public enum EmbeddingDevice
{
    /// <summary>The bundled engine on the GPU, other models on the CPU (the default).</summary>
    Auto,

    /// <summary>Every local model on the GPU where the platform has one.</summary>
    Gpu,

    /// <summary>Every local model on the CPU.</summary>
    Cpu
}

/// <summary>Parses <c>embedding.device</c> and decides whether a session tries the GPU.</summary>
public static class EmbeddingDeviceSetting
{
    /// <summary>The setting's accepted values, as the CLI and doctor spell them.</summary>
    public static readonly string[] Values = ["auto", "gpu", "cpu"];

    /// <summary>The stored value as a device; unset or unrecognized is <see cref="EmbeddingDevice.Auto" />.</summary>
    public static EmbeddingDevice Parse(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "gpu" => EmbeddingDevice.Gpu,
            "cpu" => EmbeddingDevice.Cpu,
            _ => EmbeddingDevice.Auto
        };

    /// <summary>
    ///     Auto trusts the GPU only for the bundled fp16 engine, whose GPU vectors match its CPU ones
    ///     (cosine 0.9998); a quantized user model can drift on the GPU (cosine 0.94), so it opts in.
    /// </summary>
    public static bool PrefersGpu(EmbeddingDevice device, bool isBundledEngine) =>
        device switch
        {
            EmbeddingDevice.Gpu => true,
            EmbeddingDevice.Cpu => false,
            _ => isBundledEngine
        };
}
