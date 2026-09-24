using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Measured on Apple M4 (docs/work/2026-09-24-onnx-runtime-providers-gpu-mlx.md): ORT's intra-op
///     thread pool spin-waits by default, which costs 44-59ms of CPU per embed on a WebGPU session
///     with no latency benefit. <see cref="OnnxEmbeddingGenerator.GpuSessionConfigEntries" /> is the
///     single source of the config entries <c>CreateGpuSessionOrNull</c> applies to turn that off —
///     CPU-only sessions are untouched because spinning helps CPU-bound runs.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OnnxGpuSessionConfigTests
{
    [Fact]
    public void GpuSessionConfigEntries_DisablesIntraOpSpinning()
    {
        OnnxEmbeddingGenerator.GpuSessionConfigEntries.ShouldContainKeyAndValue("session.intra_op.allow_spinning", "0");
    }
}
