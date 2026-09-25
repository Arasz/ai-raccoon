using System.Runtime.InteropServices;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0108/0115: a session that prefers the GPU runs the bundled engine on WebGPU where the loaded
///     core has it (macOS's NuGet core, the bundled core on Windows and Linux x64), with the CPU session's vectors.
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

    /// <summary>The RIDs whose package ships the WebGPU-enabled ONNX Runtime core in place of the NuGet one (ADR-0115).</summary>
    private static readonly string[] WebGpuCoreRids = ["win-x64", "win-arm64", "linux-x64"];

    /// <summary>Set to 1 on a host with a known WebGPU adapter (CI's lavapipe step) to fail instead of falling back.</summary>
    private const string RequireWebGpuVariable = "AIRACCOON_REQUIRE_WEBGPU";

    [RetryFact]
    public void ShippedCore_OnWebGpuCoreRids_HasWebGpu()
    {
        if (!WebGpuCoreRids.Contains(RuntimeInformation.RuntimeIdentifier))
        {
            Assert.Skip("only win-x64, win-arm64 and linux-x64 ship the WebGPU core");
        }

        OrtEnv.Instance().GetAvailableProviders().ShouldContain("WebGpuExecutionProvider",
            "the NuGet core is loaded; run scripts/download-webgpu-core.py before building");
    }

    /// <summary>
    ///     Off macOS, a session that prefers the GPU runs on the bundled core's WebGPU with the CPU session's
    ///     vectors, or falls back to the CPU with the reason (no adapter, or no WebGPU in this RID's core).
    /// </summary>
    [RetryFact]
    public async Task PreferGpu_OffMacOs_RunsOnWebGpu_OrFallsBackWithAReason()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("covered by the macOS test above");
        }

        using var gpu = Generator(preferGpu: true);
        if (Environment.GetEnvironmentVariable(RequireWebGpuVariable) == "1")
        {
            gpu.ExecutionProvider.ShouldBe("WebGPU");
        }

        if (gpu.ExecutionProvider != "WebGPU")
        {
            gpu.ExecutionProvider.ShouldStartWith("CPU (GPU refused: ");
            if (!WebGpuCoreRids.Contains(RuntimeInformation.RuntimeIdentifier))
            {
                gpu.ExecutionProvider.ShouldBe($"CPU (GPU refused: {OnnxEmbeddingGenerator.NoWebGpuInCoreReason})");
            }

            return;
        }

        using var cpu = Generator(preferGpu: false);
        const string text = "The drain embeds pending rows one at a time on the GPU.";
        var onGpu = await gpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        var onCpu = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        TestData.Cosine(onGpu[0].Vector, onCpu[0].Vector).ShouldBeGreaterThan(0.999);
    }

    [RetryFact]
    public async Task TwoGpuSessions_EmbeddingAtOnce_BothSucceed()
    {
        // Memory and code each hold a session; WebGPU sessions share one process-wide GPU context.
        using var first = Generator(preferGpu: true);
        if (first.ExecutionProvider != "WebGPU")
        {
            Assert.Skip("no WebGPU device is available on this host, or its ONNX Runtime core has no WebGPU");
        }

        using var second = Generator(preferGpu: true);
        var texts = Enumerable.Range(0, 24).Select(i => string.Join(' ', Enumerable.Repeat($"row {i} token", 4 + i * 5))).ToArray();

        var runs = Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? first : second)
            .GenerateAsync(texts, cancellationToken: TestContext.Current.CancellationToken));
        var results = await Task.WhenAll(runs);

        results.ShouldAllBe(r => r.Count == texts.Length);
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
