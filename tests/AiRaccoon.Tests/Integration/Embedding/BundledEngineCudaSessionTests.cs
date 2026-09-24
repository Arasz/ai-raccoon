using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Device cuda: a session tries the user-supplied onnxruntime CUDA provider library first, and any
///     refusal still builds a WebGPU or CPU session that names the CUDA reason in its execution provider.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BundledEngineCudaSessionTests
{
    private const string CudaLibraryVariable = "AIRACCOON_TEST_CUDA_LIBRARY";

    [RetryFact]
    public async Task CudaLibraryConfigured_RunsOnCuda_WithTheCpuSessionsVectors()
    {
        var library = Environment.GetEnvironmentVariable(CudaLibraryVariable);
        if (string.IsNullOrWhiteSpace(library))
        {
            Assert.Skip($"set {CudaLibraryVariable} to an onnxruntime CUDA provider library to run this on a CUDA machine");
        }

        using var cuda = Generator(library);
        using var cpu = Generator(null, preferGpu: false);
        const string text = "The drain embeds pending rows one at a time on the GPU.";

        var onCuda = await cuda.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        var onCpu = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);

        cuda.ExecutionProvider.ShouldBe("CUDA");
        TestData.Cosine(onCuda[0].Vector, onCpu[0].Vector).ShouldBeGreaterThanOrEqualTo(0.999);
    }

    [RetryFact]
    public void MissingLibrary_IsRefused_AndTheSessionIsStillBuilt()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS refuses CUDA before looking for the library");
        }

        var missing = Path.Combine(Path.GetTempPath(), $"no-such-cuda-{Guid.NewGuid():N}.so");

        using var generator = Generator(missing);

        generator.ExecutionProvider.ShouldContain($"(CUDA refused: provider library not found: {missing})");
        generator.Dimension.ShouldBeGreaterThan(0);
    }

    [RetryFact]
    public void EmptyLibrary_IsRefused_WithTheCommandThatSetsIt()
    {
        using var generator = Generator("");

        generator.ExecutionProvider.ShouldEndWith(
            "(CUDA refused: no provider library configured; run 'ai-raccoon settings model device cuda <path>')");
    }

    [RetryFact]
    public void OnMacOs_CudaIsRefused_AndWebGpuOrTheCpuRunsTheSession()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("the platform refusal is macOS-only");
        }

        using var generator = Generator("/opt/ort/libonnxruntime_providers_cuda.so");

        var provider = generator.ExecutionProvider;
        (provider.StartsWith("WebGPU (CUDA refused: ", StringComparison.Ordinal)
         || provider.StartsWith("CPU", StringComparison.Ordinal)).ShouldBeTrue(provider);
        provider.ShouldEndWith("(CUDA refused: requires Windows or Linux)");
    }

    [RetryFact]
    public void NoCudaLibrary_NeverMentionsCuda()
    {
        using var generator = Generator(null);

        generator.ExecutionProvider.ShouldNotContain("CUDA");
    }

    private static OnnxEmbeddingGenerator Generator(string? cudaLibraryPath, bool preferGpu = true)
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new Infrastructure.Embedding.Manifest.EmbeddingManifestSerializer(),
            new Infrastructure.Embedding.Manifest.EmbeddingManifestValidator()).Load(directory);
        return new OnnxEmbeddingGenerator(Path.Combine(directory, descriptor.OnnxModelFile),
            new EmbeddingTokenizerFactory().Create(descriptor, directory), descriptor, NullLogger.Instance, 0, preferGpu,
            preferMlx: false, cudaLibraryPath: cudaLibraryPath);
    }
}
