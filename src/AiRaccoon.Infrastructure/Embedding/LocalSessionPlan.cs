using AiRaccoon.Infrastructure.Embedding.NeuralEngine;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     What a local session is built to try, in order: MLX, then CUDA, then WebGPU, then the CPU, and
///     whether the Neural Engine wraps it (ADR-0118). <see cref="CoreMlRefusal" /> is set when
///     <c>device coreml</c> was asked for but this platform cannot run it.
/// </summary>
public sealed record LocalSessionPlan(bool Mlx, bool Cuda, bool Gpu, bool NeuralEngine, string? CoreMlRefusal)
{
    /// <summary>The plan for <paramref name="device" /> on this model and platform; the ANE graph's presence only matters under coreml.</summary>
    public static LocalSessionPlan For(EmbeddingDevice device, bool isBundledEngine, bool macArm64, bool coreMlGraphPresent)
    {
        var coreMl = EmbeddingDeviceSetting.PrefersCoreMl(device, isBundledEngine);
        var refusal = coreMl ? NeuralEngineEmbeddingGenerator.RefusalReason(macArm64, coreMlGraphPresent) : null;
        return new LocalSessionPlan(
            EmbeddingDeviceSetting.PrefersMlx(device, isBundledEngine),
            device == EmbeddingDevice.Cuda,
            EmbeddingDeviceSetting.PrefersGpu(device, isBundledEngine),
            coreMl && refusal is null,
            refusal);
    }
}
