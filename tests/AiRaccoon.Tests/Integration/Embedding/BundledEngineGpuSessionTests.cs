using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0108: a session that prefers the GPU runs the bundled engine on WebGPU where the platform's
///     ORT build has it (macOS), and its vectors match the CPU session's. Elsewhere it runs on the CPU.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BundledEngineGpuSessionTests
{
    [RetryFact]
    public async Task PreferGpu_OnMacOs_RunsOnWebGpu_WithTheCpuSessionsVectors()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("the standard ORT build implements WebGPU only on macOS");
        }

        using var gpu = Generator(preferGpu: true);
        using var cpu = Generator(preferGpu: false);
        const string text = "The drain embeds pending rows one at a time on the GPU.";

        var onGpu = await gpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        var onCpu = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);

        gpu.ExecutionProvider.ShouldBe("WebGPU");
        cpu.ExecutionProvider.ShouldBe("CPU");
        TestData.Cosine(onGpu[0].Vector, onCpu[0].Vector).ShouldBeGreaterThan(0.999);
    }

    [RetryFact]
    public void PreferGpuFalse_RunsOnTheCpu()
    {
        using var cpu = Generator(preferGpu: false);

        cpu.ExecutionProvider.ShouldBe("CPU");
    }

    private static OnnxEmbeddingGenerator Generator(bool preferGpu)
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new Infrastructure.Embedding.Manifest.EmbeddingManifestSerializer(),
            new Infrastructure.Embedding.Manifest.EmbeddingManifestValidator()).Load(directory);
        return new OnnxEmbeddingGenerator(Path.Combine(directory, descriptor.OnnxModelFile),
            new EmbeddingTokenizerFactory().Create(descriptor, directory), descriptor, NullLogger.Instance, 0, preferGpu);
    }
}
