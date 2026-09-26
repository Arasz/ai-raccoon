using System.Text.Json;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.NeuralEngine;

/// <summary>
///     ADR-0118: WebGPU serves while the Neural Engine sessions compile in the background; the swap
///     happens only after every bucket loads and passes the parity probe, and any failure, timeout or
///     probe miss keeps WebGPU for the rest of the process.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NeuralEngineEmbeddingGeneratorTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly string _root = TestData.CreateTempRoot("coreml-switch");
    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        foreach (var item in _cleanup)
        {
            item.Dispose();
        }

        TestData.DeleteTempRoot(_root);
    }

    [Fact]
    public async Task RowLongerThanTheLargestBucket_IsServedByTheCpuSession()
    {
        var factory = new FakeSessionFactory();
        var generator = Create(factory, new FakeWebGpu());
        (await generator.WaitUntilSettledAsync(Ct)).ShouldBe(NeuralEngineState.NeuralEngineServing);

        var result = await generator.GenerateAsync([WordTokenizer.TextOf(1025)], cancellationToken: Ct);
        await generator.GenerateAsync([WordTokenizer.TextOf(1500)], cancellationToken: Ct);

        result.Count.ShouldBe(1);
        factory.OverflowsCreated.ShouldBe(1);  // built once and reused, never one session per long row
        factory.Overflow.Rows.ShouldBe(2);
    }

    [Fact]
    public async Task RowOfTheLargestBucketLength_RunsOnThatBucket_NotTheCpuSession()
    {
        var factory = new FakeSessionFactory();
        var generator = Create(factory, new FakeWebGpu());
        await generator.WaitUntilSettledAsync(Ct);
        var before = factory.Created.Single(s => s.Bucket == 1024).Runs;

        await generator.GenerateAsync([WordTokenizer.TextOf(1024)], cancellationToken: Ct);

        factory.Created.Single(s => s.Bucket == 1024).Runs.ShouldBe(before + 1);
        factory.OverflowsCreated.ShouldBe(0);
    }

    [Fact]
    public async Task Serving_RoutesARowToItsPaddedBucket_AndNormalizesTheVector()
    {
        var factory = new FakeSessionFactory();
        var webGpu = new FakeWebGpu();
        var generator = Create(factory, webGpu);
        await generator.WaitUntilSettledAsync(Ct);
        var rowsBefore = webGpu.Rows;

        var result = await generator.GenerateAsync([WordTokenizer.TextOf(300)], cancellationToken: Ct);

        generator.ExecutionProvider.ShouldBe("CoreML");
        factory.Created.Single(s => s.Bucket == 512).Runs.ShouldBe(2, "one probe row, one served row");
        result[0].Vector.ToArray().ShouldBe([1f, 0f, 0f, 0f]);
        webGpu.Rows.ShouldBe(rowsBefore);
        webGpu.Disposed.ShouldBeTrue("the WebGPU session is released once the Neural Engine serves");
    }

    [Fact]
    public async Task SlowCompile_RowsAreServedByWebGpu_WithoutWaitingForIt()
    {
        var block = Track(new ManualResetEventSlim(false));
        var factory = new FakeSessionFactory { BlockUntil = block };
        var webGpu = new FakeWebGpu();
        var generator = Create(factory, webGpu);
        factory.Entered.Wait(Wait, Ct).ShouldBeTrue();

        var result = await generator.GenerateAsync(["a short row"], cancellationToken: Ct).WaitAsync(Wait, Ct);

        result[0].Vector.ToArray().ShouldBe(webGpu.Vector);
        generator.Switch.State.ShouldBe(NeuralEngineState.CompilingNeuralEngine);
        generator.ExecutionProvider.ShouldBe("WebGPU (CoreML compiling)");
        block.Set();
    }

    [Fact]
    public async Task ProbeMissOnOneBucketOnly_Refuses_KeepsWebGpu_AndDisposesTheCoreMlSessions()
    {
        var factory = new FakeSessionFactory { MismatchOnBucket = 768 };
        var webGpu = new FakeWebGpu();
        var generator = Create(factory, webGpu);

        (await generator.WaitUntilSettledAsync(Ct)).ShouldBe(NeuralEngineState.Refused);

        var last = generator.Switch.History[^1];
        last.Trigger.ShouldBe(NeuralEngineTrigger.ProbeFailed);
        last.Reason.ShouldContain("768");
        factory.Created.Count.ShouldBe(4);
        factory.Created.ShouldAllBe(s => s.Disposed);
        webGpu.Disposed.ShouldBeFalse();
        generator.ExecutionProvider.ShouldStartWith("WebGPU (CoreML refused: ");
        var result = await generator.GenerateAsync(["after the refusal"], cancellationToken: Ct);
        result[0].Vector.ToArray().ShouldBe(webGpu.Vector);
    }

    [Fact]
    public async Task LoadThrows_RefusesWithTheMessage_AndDisposesWhatLoaded()
    {
        var factory = new FakeSessionFactory { ThrowOnBucket = 768 };
        var generator = Create(factory, new FakeWebGpu());

        (await generator.WaitUntilSettledAsync(Ct)).ShouldBe(NeuralEngineState.Refused);

        var last = generator.Switch.History[^1];
        last.Trigger.ShouldBe(NeuralEngineTrigger.LoadFailed);
        last.Reason.ShouldContain("CoreML refused to compile bucket 768");
        factory.Created.Count.ShouldBe(2);
        factory.Created.ShouldAllBe(s => s.Disposed);
    }

    [Fact]
    public async Task NeverReturningFactory_TimesOutAtTheDeadline_AndDisposeReturnsPromptly()
    {
        var block = Track(new ManualResetEventSlim(false));
        var factory = new FakeSessionFactory { BlockUntil = block };
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var webGpu = new FakeWebGpu();
        var generator = Create(factory, webGpu, time, TimeSpan.FromMinutes(5), dispose: false);
        factory.Entered.Wait(Wait, Ct).ShouldBeTrue();

        var settled = generator.WaitUntilSettledAsync(Ct);
        while (!settled.IsCompleted)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.WhenAny(settled, Task.Delay(20, Ct));
        }

        (await settled).ShouldBe(NeuralEngineState.Refused);
        generator.Switch.History[^1].Trigger.ShouldBe(NeuralEngineTrigger.TimedOut);
        await Task.Run(generator.Dispose, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
        webGpu.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task TimedOutSessionsThatLoadLate_AreDisposed()
    {
        var block = Track(new ManualResetEventSlim(false));
        var factory = new FakeSessionFactory { BlockUntil = block, BlockBucket = 1024 };
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var generator = Create(factory, new FakeWebGpu(), time, TimeSpan.FromMinutes(5));
        factory.Entered.Wait(Wait, Ct).ShouldBeTrue();
        var settled = generator.WaitUntilSettledAsync(Ct);
        while (!settled.IsCompleted)
        {
            time.Advance(TimeSpan.FromMinutes(1));
            await Task.WhenAny(settled, Task.Delay(20, Ct));
        }

        block.Set();

        await WaitUntil(() => factory.Created.Count == 4 && factory.Created.All(s => s.Disposed));
    }

    [Fact]
    public async Task DisposeDuringACompileThatNeverReturns_ReturnsPromptly()
    {
        var block = Track(new ManualResetEventSlim(false));
        var factory = new FakeSessionFactory { BlockUntil = block };
        var generator = Create(factory, new FakeWebGpu(), dispose: false);
        factory.Entered.Wait(Wait, Ct).ShouldBeTrue();

        await Task.Run(generator.Dispose, Ct).WaitAsync(TimeSpan.FromSeconds(2), Ct);
    }

    [Fact]
    public async Task Swap_WaitsForARowInFlightOnWebGpu_BeforeDisposingIt()
    {
        var block = Track(new ManualResetEventSlim(false));
        var factory = new FakeSessionFactory { BlockUntil = block };
        var webGpu = new FakeWebGpu();
        var generator = Create(factory, webGpu);
        factory.Entered.Wait(Wait, Ct).ShouldBeTrue();
        await WaitUntil(() => webGpu.Rows >= 4);

        var inFlight = generator.GenerateAsync([FakeWebGpu.HoldText], cancellationToken: Ct);
        webGpu.Holding.Wait(Wait, Ct).ShouldBeTrue();
        block.Set();
        await WaitUntil(() => factory.Created.Count == 4 && factory.Created.All(s => s.Runs == 1));
        await Task.Delay(100, Ct);
        webGpu.Release.Set();

        (await inFlight)[0].Vector.ToArray().ShouldBe(webGpu.Vector);
        (await generator.WaitUntilSettledAsync(Ct)).ShouldBe(NeuralEngineState.NeuralEngineServing);
        webGpu.Disposed.ShouldBeTrue();
        webGpu.DisposedWhileInFlight.ShouldBeFalse();
    }

    [Fact]
    public async Task EveryTransition_WritesTheStatusFile_WithStateTriggerAndPid()
    {
        var block = Track(new ManualResetEventSlim(false));
        var factory = new FakeSessionFactory { BlockUntil = block };
        var generator = Create(factory, new FakeWebGpu());
        factory.Entered.Wait(Wait, Ct).ShouldBeTrue();

        var compiling = ReadStatus();
        block.Set();
        await generator.WaitUntilSettledAsync(Ct);
        var serving = ReadStatus();

        compiling.GetProperty("state").GetString().ShouldBe("CompilingNeuralEngine");
        compiling.GetProperty("trigger").GetString().ShouldBe("Started");
        compiling.GetProperty("pid").GetInt32().ShouldBe(Environment.ProcessId);
        serving.GetProperty("state").GetString().ShouldBe("NeuralEngineServing");
        serving.GetProperty("trigger").GetString().ShouldBe("SessionsLoadedAndProbePassed");
        serving.TryGetProperty("reason", out _).ShouldBeTrue();
        serving.TryGetProperty("at", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task ProbePass_MarksTheCacheComplete()
    {
        var generator = Create(new FakeSessionFactory(), new FakeWebGpu());

        await generator.WaitUntilSettledAsync(Ct);

        File.Exists(Path.Combine(CacheRoot, "abcdef012345", "1.30.0", CoreMlCache.CompleteMarkerName)).ShouldBeTrue();
    }

    [Fact]
    public async Task ProbeMiss_LeavesTheCacheUnmarked()
    {
        var generator = Create(new FakeSessionFactory { MismatchOnBucket = 256 }, new FakeWebGpu());

        await generator.WaitUntilSettledAsync(Ct);

        File.Exists(Path.Combine(CacheRoot, "abcdef012345", "1.30.0", CoreMlCache.CompleteMarkerName)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, true, "requires macOS on Apple Silicon")]
    [InlineData(false, false, "requires macOS on Apple Silicon")]
    [InlineData(true, false, "graph missing")]
    public void RefusalReason_NamesWhyThePlatformCannotRunIt(bool macArm64, bool graphPresent, string expected) =>
        NeuralEngineEmbeddingGenerator.RefusalReason(macArm64, graphPresent).ShouldBe(expected);

    [Fact]
    public void RefusalReason_IsNullWhereItCanRun() =>
        NeuralEngineEmbeddingGenerator.RefusalReason(true, true).ShouldBeNull();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CacheRoot => Path.Combine(_root, "coreml-cache");

    private JsonElement ReadStatus()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CacheRoot, CoreMlCache.StatusFileName)));
        return document.RootElement.Clone();
    }

    private NeuralEngineEmbeddingGenerator Create(FakeSessionFactory factory, FakeWebGpu webGpu,
        TimeProvider? time = null, TimeSpan? deadline = null, bool dispose = true)
    {
        var cache = new CoreMlCache(CacheRoot, "abcdef012345", "1.30.0", NullLogger.Instance);
        var generator = new NeuralEngineEmbeddingGenerator(webGpu, new WordTokenizer(), true, factory, cache,
            time ?? TimeProvider.System, deadline ?? TimeSpan.FromMinutes(1), NullLogger.Instance);
        if (dispose)
        {
            _cleanup.Add(generator);
        }

        return generator;
    }

    private ManualResetEventSlim Track(ManualResetEventSlim gate)
    {
        _cleanup.Insert(0, new Releaser(gate));
        return gate;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "the condition never became true");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class Releaser(ManualResetEventSlim gate) : IDisposable
    {
        public void Dispose() => gate.Set();
    }
}
