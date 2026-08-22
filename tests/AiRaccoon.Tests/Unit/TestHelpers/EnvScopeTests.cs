using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Pins the three defects the hand-rolled env blocks carried: a gate taken without a token,
///     a release that a failing restore could skip, and a variable restored to null instead of
///     to the value it had.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EnvScopeTests
{
    private const string HadAValue = "AIRACCOON_ENVSCOPE_PROBE_A";
    private const string WasUnset = "AIRACCOON_ENVSCOPE_PROBE_B";
    private const string OverriddenToNull = "AIRACCOON_ENVSCOPE_PROBE_C";

    // Names carrying '=' cannot be set, so applying and restoring one both throw.
    private const string Unsettable = "AIRACCOON=ENVSCOPE";

    [Fact]
    public async Task Dispose_RestoresEveryVariable_ToWhatItHeldBefore()
    {
        // OtlpTraceExportE2ETests restored OTEL_TRACES_SAMPLER to null rather than to its
        // original: OverriddenToNull is that exact shape, and it must come back.
        Environment.SetEnvironmentVariable(HadAValue, "original-a");
        Environment.SetEnvironmentVariable(WasUnset, null);
        Environment.SetEnvironmentVariable(OverriddenToNull, "original-c");
        try
        {
            var scope = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
                (HadAValue, "overridden"), (WasUnset, "overridden"), (OverriddenToNull, null));
            Environment.GetEnvironmentVariable(HadAValue).ShouldBe("overridden");
            Environment.GetEnvironmentVariable(WasUnset).ShouldBe("overridden");
            Environment.GetEnvironmentVariable(OverriddenToNull).ShouldBeNull();

            await scope.DisposeAsync();

            Environment.GetEnvironmentVariable(HadAValue).ShouldBe("original-a");
            Environment.GetEnvironmentVariable(WasUnset).ShouldBeNull();
            Environment.GetEnvironmentVariable(OverriddenToNull).ShouldBe("original-c");
        }
        finally
        {
            Environment.SetEnvironmentVariable(HadAValue, null);
            Environment.SetEnvironmentVariable(WasUnset, null);
            Environment.SetEnvironmentVariable(OverriddenToNull, null);
        }
    }

    [Fact]
    public async Task WhenARestoreThrows_TheGateIsStillReleased()
    {
        await Should.ThrowAsync<ArgumentException>(async () => await EnvScope.AcquireAsync(
            TestContext.Current.CancellationToken, (HadAValue, "applied"), (Unsettable, "boom")));

        // A stranded gate is never reacquirable: completing this wait IS the assertion, and only the
        // test's token ends a genuine strand (no wall-clock verdict, PR #464).
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        // Balanced either way: the permit we just took, or the one EnvScope leaked.
        TestData.EnvVarGate.Release();
    }

    [Fact]
    public async Task AcquireAsync_WhenTheGateIsHeld_HonoursTheToken_RatherThanBlockingForever()
    {
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        Task<EnvScope>? acquire = null;
        try
        {
            using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            acquire = EnvScope.AcquireAsync(caller.Token, (HadAValue, "x")).AsTask();

            // A negative check ("never completes") needs a window by nature; this bound can only
            // pass vacuously under load, never fail a correct build (PR #464).
            await Should.ThrowAsync<OperationCanceledException>(() =>
                acquire.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            // Salvage: if an untokened wait does eventually win the gate, give it straight back.
            _ = acquire?.ContinueWith(static won => won.Result.DisposeAsync(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            TestData.EnvVarGate.Release();
        }
    }

    [Fact]
    public async Task DisposingTwice_ReleasesTheGateOnce()
    {
        var scope = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken, (HadAValue, "x"));
        await scope.DisposeAsync();

        // A second release on a (1,1) semaphore throws, and a swallowed one would hand the gate
        // to two holders at once.
        await Should.NotThrowAsync(async () => await scope.DisposeAsync());
    }
}
