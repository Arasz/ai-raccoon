using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using Xunit.Sdk;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0110: a session that prefers MLX runs the bundled engine's rewritten graph on the
///     onnxruntime MLX plugin EP where the plugin files and the graph are both present (osx-arm64
///     only), and its vectors match the CPU session's. Elsewhere — or when the files are absent,
///     which they are in every environment this test suite runs in unless
///     scripts/download-mlx-runtime.py was run against an osx-arm64 build — it falls back to the
///     existing WebGPU-then-CPU path and says so in <see cref="OnnxEmbeddingGenerator.ExecutionProvider" />.
///     The MLX-required cases skip fail-closed (review F2): with AIRACCOON_REQUIRE_MLX=1 set — the
///     named MLX run sets it — an absent plugin is a FAILURE, never a silent skip.
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
            SkipWhenMlxMissing("the MLX plugin runtime and/or the rewritten graph are not present next to this build");
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

    /// <summary>
    ///     The padding precondition pin (review F2), on a SHORT row: config D's full-budget row is
    ///     1022 + [CLS]/[SEP] = 1024 = one exact bucket, so PaddedLength leaves it alone with or
    ///     without padding and a full-budget assertion could never fail. A 3-token row pads to 64
    ///     only if the MLX session really buckets rows (OnnxEmbeddingGenerator._bucketRows); RED
    ///     when _bucketRows = true is removed from the real MLX session path — the row then runs
    ///     unpadded and reports 3.
    /// </summary>
    [RetryFact]
    public async Task PreferMlx_FilesPresent_AShortRow_PadsToOneBucket_WithTheCpuSessionsVectors()
    {
        var directory = BundledModel.ResolveDirectory();
        var descriptor = new EmbeddingManifestLoader(new Infrastructure.Embedding.Manifest.EmbeddingManifestSerializer(),
            new Infrastructure.Embedding.Manifest.EmbeddingManifestValidator()).Load(directory);
        var modelPath = Path.Combine(directory, descriptor.OnnxModelFile);
        if (!OperatingSystem.IsMacOS()
            || OnnxEmbeddingGenerator.ResolveMlxPluginDirectory(AppContext.BaseDirectory) is null
            || OnnxEmbeddingGenerator.ResolveMlxGraphPath(modelPath) is null)
        {
            SkipWhenMlxMissing("the MLX plugin runtime and/or the rewritten graph are not present next to this build");
        }

        using var mlx = Generator(preferMlx: true);
        using var cpu = Generator(preferMlx: false);
        const string text = "a";

        var onMlx = await mlx.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
        var onCpu = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);

        mlx.ExecutionProvider.ShouldBe("MLX");
        cpu.LastSequenceLength.ShouldBe(3,
            "premise: [CLS] a [SEP] must be a 3-token row, or this pin is not measuring a short row");
        mlx.LastSequenceLength.ShouldBe(64,
            "an MLX session pads every row to the next 64-token bucket (ADR-0114); unpadded this reports 3");
        TestData.Cosine(onMlx[0].Vector, onCpu[0].Vector).ShouldBeGreaterThanOrEqualTo(0.9999);
    }

    /// <summary>The fail-closed skip (review F2 defect 1): AIRACCOON_REQUIRE_MLX=1 turns the skip
    /// into a failure, so the named MLX run cannot go green without exercising MLX.</summary>
    [Fact]
    public async Task MlxSkipPolicy_RequireFlagSet_TurnsTheSkipIntoAFailure()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (RequireMlxEnvVar, "1"));

        Should.Throw<FailException>(() => SkipWhenMlxMissing("the plugin is absent"))
            .Message.ShouldContain(RequireMlxEnvVar);
    }

    /// <summary>Without the flag the ordinary skip stands — non-MLX hosts keep passing the suite.</summary>
    [Fact]
    public async Task MlxSkipPolicy_NoRequireFlag_StaysAnOrdinarySkip()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (RequireMlxEnvVar, null));

        Should.Throw<SkipException>(() => SkipWhenMlxMissing("the plugin is absent"));
    }

    internal const string RequireMlxEnvVar = "AIRACCOON_REQUIRE_MLX";

    /// <summary>Skips the case unless AIRACCOON_REQUIRE_MLX=1 demands a real MLX run — then the
    /// missing precondition is a failure (fail-closed skip, review F2).</summary>
    internal static void SkipWhenMlxMissing(string reason)
    {
        if (Environment.GetEnvironmentVariable(RequireMlxEnvVar) == "1")
        {
            Assert.Fail($"{reason} — and {RequireMlxEnvVar}=1 demands a real MLX run, so this fails instead of skipping");
        }

        Assert.Skip(reason);
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
