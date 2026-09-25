using AiRaccoon.Core.Embedding;

namespace AiRaccoon.Infrastructure.Embedding.NeuralEngine;

/// <summary>The bundled engine's ANE-layout graph and the fixed shapes its CoreML sessions compile (ADR-0118).</summary>
internal static class CoreMlGraph
{
    /// <summary>The graph file, beside <c>model_fp16.onnx</c> so it can read that graph's weights file.</summary>
    public const string FileName = "model_fp16_ane.onnx";

    /// <summary>The weights file the graph reads, pinned in the bundled manifest.</summary>
    public const string WeightsFileName = "model_fp16.onnx_data";

    public const string BatchDimension = "batch_size";

    public const string SequenceDimension = "sequence_length";

    /// <summary>The attention mask's own sequence dimension, overridden to the same bucket length.</summary>
    public const string MaskSequenceDimension = "total_sequence_length";

    /// <summary>One compiled session per bucket length, in tokens.</summary>
    public static IReadOnlyList<int> Buckets { get; } =
        [.. Enumerable.Range(1, LengthBuckets.CoreMlWindow / LengthBuckets.CoreMlStep).Select(i => i * LengthBuckets.CoreMlStep)];
}
