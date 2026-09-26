using System.Diagnostics;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0118 "Switching devices": a switch made while the Neural Engine sessions are still compiling.
///     The abandoned compile stops at its next bucket and leaves an unmarked set. MLX serves next, and
///     the next CoreML start deletes that set and compiles it again. macOS on Apple Silicon, with the
///     MLX plugin and WebGPU, only.
/// </summary>
[Collection(NeuralEngineCollection.Name)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DeviceSwitchMidCompileTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan CompileStartsWithin = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AbandonedCompileStopsWithin = TimeSpan.FromMinutes(2);
    private readonly string _dataRoot = TestData.CreateTempRoot("device-switch-mid-compile");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task DisposeWhileCompiling_ThenMlx_ThenCoreMl_RecompilesTheUnmarkedSet()
    {
        DeviceSwitchTests.SkipUnlessEveryDeviceCanRun();
        var ct = TestContext.Current.CancellationToken;
        var rows = DeviceSwitchTests.Rows();
        var reference = await DeviceSwitchTests.CpuReferenceAsync(rows, ct);
        var cacheRoot = Path.Combine(_dataRoot, EmbeddingService.CoreMlCacheDirectoryName);

        var (first, _) = DeviceSwitchTests.Build("coreml", _dataRoot);
        var abandoned = first.ShouldBeOfType<NeuralEngineEmbeddingGenerator>();
        var firstBucket = await WaitForBucketsAsync(cacheRoot, 2, ct);
        var abandonedAt = Directory.GetCreationTimeUtc(firstBucket);
        abandoned.Switch.State.ShouldBe(NeuralEngineState.CompilingNeuralEngine);
        var clock = Stopwatch.StartNew();
        abandoned.Dispose();
        var disposeSeconds = clock.Elapsed.TotalSeconds;
        var startedBuckets = Buckets(cacheRoot).Length;
        (await abandoned.WaitUntilSettledAsync(ct)).ShouldBe(NeuralEngineState.CompilingNeuralEngine);

        var (mlx, _) = DeviceSwitchTests.Build("mlx", _dataRoot);
        mlx.ExecutionProvider.ShouldBe("MLX");
        var mlxCosine = DeviceSwitchTests.MinCosine(await mlx.GenerateAsync(rows, cancellationToken: ct), reference);
        mlxCosine.ShouldBeGreaterThanOrEqualTo(0.999);
        mlx.Dispose();
        MarkerFiles(cacheRoot).ShouldBeEmpty("the abandoned compile must not mark its set complete");

        clock.Restart();
        var (second, logger) = DeviceSwitchTests.Build("coreml", _dataRoot);
        var stillCompiling = !abandoned.Compile.IsCompleted;
        var recompiled = second.ShouldBeOfType<NeuralEngineEmbeddingGenerator>();
        var settled = await DeviceSwitchTests.SettleAsync(recompiled, logger, ct);
        var settleSeconds = clock.Elapsed.TotalSeconds;
        (await Task.WhenAny(abandoned.Compile, Task.Delay(AbandonedCompileStopsWithin, ct))).ShouldBe(abandoned.Compile,
            "the abandoned compile never stopped");

        settled.ShouldContain("sessions compiled");
        Directory.GetCreationTimeUtc(firstBucket).ShouldBeGreaterThan(abandonedAt, "the half-written set was reused, not deleted");
        second.ExecutionProvider.ShouldBe("CoreML");
        var coreMlCosine = DeviceSwitchTests.MinCosine(await second.GenerateAsync(rows, cancellationToken: ct), reference);
        coreMlCosine.ShouldBeGreaterThanOrEqualTo(0.999);
        MarkerFiles(cacheRoot).Length.ShouldBe(1);
        second.Dispose();
        recompiled.Compile.IsCompleted.ShouldBeTrue();
        OnnxEmbeddingGenerator.GpuGateIsFree().ShouldBeTrue();

        output.WriteLine($"abandoned compile: disposed in {disposeSeconds:F2} s with {startedBuckets} bucket directories started, " +
                         $"still compiling when coreml restarted: {stillCompiling}");
        output.WriteLine($"mlx: provider MLX, cosine min {mlxCosine:F6}");
        output.WriteLine($"coreml again: provider CoreML, cosine min {coreMlCosine:F6}, ready in {settleSeconds:F1} s, {settled}");
    }

    /// <summary>Waits until the compile has started <paramref name="count" /> bucket directories, so it is mid-set; returns the first.</summary>
    private static async Task<string> WaitForBucketsAsync(string cacheRoot, int count, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (Buckets(cacheRoot).Length < count)
        {
            clock.Elapsed.ShouldBeLessThan(CompileStartsWithin, $"the compile never started {count} buckets");
            await Task.Delay(100, ct);
        }

        return Buckets(cacheRoot).Single(path => Path.GetFileName(path) == $"bucket-{CoreMlGraph.Buckets[0]}");
    }

    private static string[] Buckets(string cacheRoot) =>
        Directory.Exists(cacheRoot)
            ? Directory.GetDirectories(cacheRoot, "bucket-*", SearchOption.AllDirectories)
            : [];

    private static string[] MarkerFiles(string cacheRoot) =>
        Directory.GetFiles(cacheRoot, CoreMlCache.CompleteMarkerName, SearchOption.AllDirectories);
}
