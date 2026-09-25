using System.Runtime.InteropServices;
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

    /// <summary>The RIDs the WebGPU plugin package ships a native library for, off macOS (ADR-0112).</summary>
    private static readonly string[] PluginRids = ["win-x64", "win-arm64", "linux-x64", "linux-arm64"];

    /// <summary>
    ///     Off macOS the WebGPU plugin shipped under webgpu/ runs the session, or the session falls back
    ///     to the CPU with the reason. On a RID the plugin ships for, the plugin must load and reach
    ///     adapter discovery: no GPU device, or a device Dawn has no driver for (CI's Hyper-V display).
    /// </summary>
    [RetryFact]
    public async Task PreferGpu_OffMacOs_RunsOnThePluginWebGpu_OrFallsBackWithAReason()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS uses the built-in WebGPU provider, not the plugin");
        }

        using var gpu = Generator(preferGpu: true);

        if (gpu.ExecutionProvider == "WebGPU")
        {
            using var cpu = Generator(preferGpu: false);
            const string text = "The drain embeds pending rows one at a time on the GPU.";
            var onGpu = await gpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
            var onCpu = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
            TestData.Cosine(onGpu[0].Vector, onCpu[0].Vector).ShouldBeGreaterThan(0.999);
            return;
        }

        gpu.ExecutionProvider.ShouldStartWith("CPU (GPU refused: ");
        if (PluginRids.Contains(RuntimeInformation.RuntimeIdentifier))
        {
            gpu.ExecutionProvider.ShouldMatch(
                @"^CPU \(GPU refused: (no WebGPU GPU device|.*Failed to get a WebGPU adapter)");
        }
    }

    [RetryFact]
    public async Task TwoGpuSessions_EmbeddingAtOnce_BothSucceed()
    {
        // Memory and code each hold a session; WebGPU sessions share one process-wide GPU context.
        using var first = Generator(preferGpu: true);
        if (first.ExecutionProvider != "WebGPU")
        {
            Assert.Skip("no WebGPU device is available on this host (built-in on macOS, plugin elsewhere)");
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
