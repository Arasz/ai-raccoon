using System.Reflection;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.ML.OnnxRuntime;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CudaRuntimeFallbackTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NativeFailure_OnFirstOrLaterRun_RetriesAndPermanentlyFallsBack(int successfulRuns)
    {
        var logger = new FakeLogger<OnnxEmbeddingGenerator>();
        using var generator = Generator(logger);
        using var cpu = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        var failure = new DllNotFoundException("LoadLibrary failed for cudnn64_9.dll");
        var calls = 0;
        AttachCuda(generator, () => calls++ < successfulRuns ? null : failure);
        const string text = "CUDA dependencies are loaded at inference time.";
        var expected = await cpu.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < successfulRuns + 2; i++)
        {
            var actual = await generator.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken);
            actual[0].Vector.ToArray().ShouldBe(expected[0].Vector.ToArray());
        }

        calls.ShouldBe(successfulRuns + 1);
        generator.ExecutionProvider.ShouldBe("CPU (CUDA refused: LoadLibrary failed for cudnn64_9.dll)");
        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Level.ShouldBe(LogLevel.Warning);
        record.Exception.ShouldBeSameAs(failure);
        record.Message.ShouldContain("CPU");
    }

    [Theory]
    [InlineData(6, "[ErrorCode:RuntimeException] Non-zero status code returned while running CUDA kernel: LoadLibrary failed for cudnn64_9.dll")]
    [InlineData(1, "[ErrorCode:Fail] Failed to load onnxruntime_providers_cuda.dll")]
    [InlineData(11, "[ErrorCode:EPFail] CUDA failure 100: no CUDA-capable device is detected")]
    public async Task OrtCudaFailure_RetriesOnCpu(int code, string message)
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        AttachCuda(generator, () => OrtException(code, message));

        (await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken)).Count.ShouldBe(1);

        generator.ExecutionProvider.ShouldStartWith("CPU (CUDA refused: ");
    }

    [Fact]
    public async Task OrtInvalidArgument_FailsAgainOnFallbackAndPropagates()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        var calls = 0;
        generator.AttachCudaForTesting((session, feed) =>
        {
            calls++;
            feed.RemoveAll(value => value.Name == "input_ids");
            return session.Run(feed);
        });

        var failure = await Should.ThrowAsync<OnnxRuntimeException>(() => generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("input_ids");
        calls.ShouldBe(2);
        generator.ExecutionProvider.ShouldStartWith("CPU (CUDA refused: ");
    }

    [Fact]
    public async Task FallbackInferenceFailure_PropagatesWithoutRetryingOrReturningToCuda()
    {
        var logger = new FakeLogger<OnnxEmbeddingGenerator>();
        using var generator = Generator(logger);
        var cudaCalls = 0;
        var fallbackCalls = 0;
        var failure = new DllNotFoundException("fallback native library missing");
        AttachCuda(generator, () => { cudaCalls++; return new DllNotFoundException("cuDNN"); },
            fallbackFailure: () => { fallbackCalls++; return failure; });

        (await Should.ThrowAsync<DllNotFoundException>(() => generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken))).ShouldBeSameAs(failure);

        cudaCalls.ShouldBe(1);
        fallbackCalls.ShouldBe(1);
        generator.ExecutionProvider.ShouldBe("CPU (CUDA refused: cuDNN)");
        logger.Collector.GetSnapshot().Count.ShouldBe(1);
    }

    [Fact]
    public async Task GpuPreference_IsRetainedAndFallbackUsesTheCorrespondingGpuGate()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>(), preferGpu: true);
        bool? gateWasFree = null;
        generator.AttachCudaForTesting((session, feed) =>
        {
            if (generator.ExecutionProvider.StartsWith("CUDA", StringComparison.Ordinal))
            {
                throw new DllNotFoundException("cuDNN");
            }

            gateWasFree = Task.Factory.StartNew(OnnxEmbeddingGenerator.GpuGateIsFree,
                TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Result;
            return session.Run(feed);
        });

        (await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken)).Count.ShouldBe(1);

        var provider = generator.ExecutionProvider;
        (provider.StartsWith("WebGPU", StringComparison.Ordinal) || provider.StartsWith("CPU (GPU refused:", StringComparison.Ordinal)).ShouldBeTrue(provider);
        gateWasFree.ShouldBe(!provider.StartsWith("WebGPU", StringComparison.Ordinal));
        provider.ShouldContain("(CUDA refused: cuDNN)");
    }

    [Fact]
    public async Task RuntimeFallback_PreservesEarlierDeviceRefusals()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        AttachCuda(generator, () => new DllNotFoundException("cuDNN"));
        generator.AppendRefusal("CoreML", "unavailable");

        await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);

        generator.ExecutionProvider.ShouldBe("CPU (CoreML refused: unavailable) (CUDA refused: cuDNN)");
    }

    [Fact]
    public async Task ConcurrentCalls_TransitionOnlyOnce_AndAllProduceCpuVectors()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        using var cpu = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        var calls = 0;
        var transitions = 0;
        AttachCuda(generator, () => { Interlocked.Increment(ref calls); return new DllNotFoundException("cuDNN"); },
            () => Interlocked.Increment(ref transitions));
        var expected = await cpu.GenerateAsync(["hello world"], cancellationToken: TestContext.Current.CancellationToken);

        var actual = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => generator.GenerateAsync(["hello world"], cancellationToken: TestContext.Current.CancellationToken)));

        calls.ShouldBe(1);
        transitions.ShouldBe(1);
        foreach (var result in actual)
        {
            result[0].Vector.ToArray().ShouldBe(expected[0].Vector.ToArray());
        }
    }

    [Fact]
    public async Task NonProviderError_PropagatesWithoutFallback()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        var failure = new InvalidOperationException("invalid application state");
        AttachCuda(generator, () => failure);

        var actual = await Should.ThrowAsync<InvalidOperationException>(() => generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken));

        actual.ShouldBeSameAs(failure);
        generator.ExecutionProvider.ShouldBe("CUDA");
    }

    [Fact]
    public async Task CanceledRequest_DoesNotRunOrFallback()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        var calls = 0;
        AttachCuda(generator, () => { calls++; return new DllNotFoundException("cuDNN"); });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => generator.GenerateAsync(["hello"], cancellationToken: cancellation.Token));

        calls.ShouldBe(0);
        generator.ExecutionProvider.ShouldBe("CUDA");
    }

    [Fact]
    public async Task FailedFallbackConstruction_PreservesCudaSessionAndPropagates()
    {
        using var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        var failure = new InvalidOperationException("fallback unavailable");
        var failRun = true;
        AttachCuda(generator, () => failRun ? new DllNotFoundException("cuDNN") : null, () => throw failure);

        (await Should.ThrowAsync<InvalidOperationException>(() => generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken))).ShouldBeSameAs(failure);
        generator.ExecutionProvider.ShouldBe("CUDA");
        failRun = false;
        (await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispose_WaitsForRunAndIsIdempotent()
    {
        var generator = Generator(new FakeLogger<OnnxEmbeddingGenerator>());
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        AttachCuda(generator, () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue(); return null; });
        var run = generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
        entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        using var disposalStarted = new ManualResetEventSlim();
        using var disposalCompleted = new ManualResetEventSlim();
        var disposal = Task.Factory.StartNew(() =>
        {
            disposalStarted.Set();
            generator.Dispose();
            disposalCompleted.Set();
        }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        disposalStarted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        try
        {
            disposalCompleted.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken).ShouldBeFalse();
        }
        finally
        {
            release.Set();
        }

        (await run).Count.ShouldBe(1);
        await disposal;
        Should.NotThrow(generator.Dispose);
        await Should.ThrowAsync<ObjectDisposedException>(() => generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken));
    }

    private static OnnxEmbeddingGenerator Generator(ILogger logger, bool preferGpu = false) => new(TestData.MiniLmModelPath(),
        WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()), EmbeddingService.BundledDescriptor, logger, preferGpu: preferGpu);

    private static void AttachCuda(OnnxEmbeddingGenerator generator, Func<Exception?> failure,
        Action? beforeFallback = null, Func<Exception?>? fallbackFailure = null)
    {
        generator.AttachCudaForTesting((session, feed) =>
        {
            var exception = generator.ExecutionProvider.StartsWith("CUDA", StringComparison.Ordinal)
                ? failure()
                : fallbackFailure?.Invoke();
            if (exception is not null)
            {
                throw exception;
            }

            return session.Run(feed);
        }, beforeFallback);
    }

    private static OnnxRuntimeException OrtException(int code, string message)
    {
        var constructor = typeof(OnnxRuntimeException).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(c => c.GetParameters().Length == 2);
        var nativeCode = Enum.ToObject(constructor.GetParameters()[0].ParameterType, code);
        return (OnnxRuntimeException)constructor.Invoke([nativeCode, message]);
    }
}
