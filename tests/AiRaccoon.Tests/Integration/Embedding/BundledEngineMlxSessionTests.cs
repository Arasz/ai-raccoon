using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0110: a session that prefers MLX runs the bundled engine's rewritten graph on the
///     onnxruntime MLX plugin EP where the plugin files and the graph are both present (osx-arm64
///     only), and its vectors match the CPU session's. Elsewhere — or when the files are absent,
///     which they are in every environment this test suite runs in unless
///     scripts/download-mlx-runtime.py was run against an osx-arm64 build — it falls back to the
///     existing WebGPU-then-CPU path and says so in <see cref="OnnxEmbeddingGenerator.ExecutionProvider" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BundledEngineMlxSessionTests
{
    [RetryFact]
    public async Task PreferMlx_FilesPresent_RunsOnMlx_WithTheCpuSessionsVectors()
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new Infrastructure.Embedding.Manifest.EmbeddingManifestSerializer(),
            new Infrastructure.Embedding.Manifest.EmbeddingManifestValidator()).Load(directory);
        var modelPath = Path.Combine(directory, descriptor.OnnxModelFile);
        if (!OperatingSystem.IsMacOS()
            || OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(AppContext.BaseDirectory) is null
            || OnnxEmbeddingGenerator.ResolveMlxGraphPath(modelPath) is null)
        {
            Assert.Skip("the MLX plugin runtime and/or the rewritten graph are not present next to this build");
        }

        using var mlx = Generator(preferMlx: true);
        using var cpu = Generator(preferMlx: false);
        const string text = "The drain embeds pending rows one at a time on the GPU.";

        var onMlx = await mlx.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        var onCpu = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);

        mlx.ExecutionProvider.ShouldBe("MLX");
        mlx.MlxCacheLimitApplied.ShouldBeTrue();
        (mlx.LastSequenceLength % 64).ShouldBe(0);
        TestData.Cosine(onMlx[0].Vector, onCpu[0].Vector).ShouldBeGreaterThanOrEqualTo(0.9999);
    }

    [RetryFact]
    public void PreferMlx_FilesAbsent_FallsBackToGpuOrCpu_AndRecordsTheRefusal()
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new Infrastructure.Embedding.Manifest.EmbeddingManifestSerializer(),
            new Infrastructure.Embedding.Manifest.EmbeddingManifestValidator()).Load(directory);
        var modelPath = Path.Combine(directory, descriptor.OnnxModelFile);
        if (OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(AppContext.BaseDirectory) is not null)
        {
            Assert.Skip("this build has the MLX plugin runtime next to it — the refusal path is not exercised here");
        }

        // preferGpu: true mirrors EmbeddingService's real wiring (PrefersGpu(Mlx, bundled) is true)
        // — a refused MLX attempt still tries WebGPU before the CPU.
        using var generator = Generator(preferMlx: true, preferGpu: true);

        generator.ExecutionProvider.ShouldContain("MLX refused");
        generator.ExecutionProvider.ShouldNotBe("MLX");
    }

    private static OnnxEmbeddingGenerator Generator(bool preferMlx, bool preferGpu = false)
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new Infrastructure.Embedding.Manifest.EmbeddingManifestSerializer(),
            new Infrastructure.Embedding.Manifest.EmbeddingManifestValidator()).Load(directory);
        return new OnnxEmbeddingGenerator(Path.Combine(directory, descriptor.OnnxModelFile),
            new EmbeddingTokenizerFactory().Create(descriptor, directory), descriptor, NullLogger.Instance, 0, preferGpu, preferMlx);
    }
}
