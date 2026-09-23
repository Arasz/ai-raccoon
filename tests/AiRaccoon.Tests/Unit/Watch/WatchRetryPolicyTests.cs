using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Per-watch consecutive-failure counter, exponential backoff, 5th failure → stopped (docs/plans/file-watcher-implementation.md, feature rule 14).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchRetryPolicyTests
{
    private static readonly DateTimeOffset T0 = WatchTestStack.FixedNow;

    [Fact]
    public void BackoffFor_IsExponential_OneTwoFourEightSeconds()
    {
        WatchRetryPolicy.BackoffFor(1).ShouldBe(TimeSpan.FromSeconds(1));
        WatchRetryPolicy.BackoffFor(2).ShouldBe(TimeSpan.FromSeconds(2));
        WatchRetryPolicy.BackoffFor(3).ShouldBe(TimeSpan.FromSeconds(4));
        WatchRetryPolicy.BackoffFor(4).ShouldBe(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public void ShouldAttempt_NeverFailed_ReturnsTrue()
    {
        var policy = new WatchRetryPolicy();
        policy.ShouldAttempt("acme", "/repo", T0).ShouldBeTrue();
    }

    [Fact]
    public void RecordFailure_FirstFailure_RetryingWithOneSecondBackoff()
    {
        var policy = new WatchRetryPolicy();
        policy.RecordFailure("acme", "/repo", T0).ShouldBe(WatchState.Retrying);
        policy.ShouldAttempt("acme", "/repo", T0).ShouldBeFalse();
        policy.ShouldAttempt("acme", "/repo", T0 + TimeSpan.FromSeconds(1)).ShouldBeTrue();
    }

    [Fact]
    public void RecordFailure_BackoffDoublesPerFailure()
    {
        var policy = new WatchRetryPolicy();
        var next = T0;
        for (var failure = 1; failure <= 4; failure++)
        {
            policy.RecordFailure("acme", "/repo", next).ShouldBe(WatchState.Retrying);
            policy.ShouldAttempt("acme", "/repo", next).ShouldBeFalse();
            next += WatchRetryPolicy.BackoffFor(failure);
            policy.ShouldAttempt("acme", "/repo", next).ShouldBeTrue();
        }
    }

    [Fact]
    public void RecordFailure_FifthFailure_StopsCheckingForever()
    {
        var policy = new WatchRetryPolicy();
        var next = T0;
        for (var failure = 1; failure <= 4; failure++)
        {
            policy.RecordFailure("acme", "/repo", next).ShouldBe(WatchState.Retrying);
            next += WatchRetryPolicy.BackoffFor(failure);
        }

        policy.RecordFailure("acme", "/repo", next).ShouldBe(WatchState.Stopped);
        policy.ShouldAttempt("acme", "/repo", next + TimeSpan.FromDays(1)).ShouldBeFalse();
        policy.IsStopped("acme", "/repo").ShouldBeTrue();
    }

    [Fact]
    public void RecordSuccess_ResetsCounter_SoLaterFailuresDoNotStopPrematurely()
    {
        var policy = new WatchRetryPolicy();
        var next = T0;
        policy.RecordFailure("acme", "/repo", next);
        next += WatchRetryPolicy.BackoffFor(1);
        policy.RecordFailure("acme", "/repo", next);
        policy.RecordSuccess("acme", "/repo");

        next += WatchRetryPolicy.BackoffFor(2);
        policy.RecordFailure("acme", "/repo", next).ShouldBe(WatchState.Retrying);
        next += WatchRetryPolicy.BackoffFor(1);
        policy.RecordFailure("acme", "/repo", next).ShouldBe(WatchState.Retrying);
        policy.IsStopped("acme", "/repo").ShouldBeFalse();
    }

    [Fact]
    public void RecordFailure_IsPerWatch_NotSharedAcrossWatches()
    {
        var policy = new WatchRetryPolicy();
        for (var failure = 1; failure <= 4; failure++)
        {
            policy.RecordFailure("acme", "/flood", T0).ShouldBe(WatchState.Retrying);
        }

        policy.RecordFailure("acme", "/flood", T0).ShouldBe(WatchState.Stopped);
        policy.ShouldAttempt("acme", "/small", T0).ShouldBeTrue();
    }

    [Fact]
    public void Forget_RemovesState_SoAReaddedWatchStartsClean()
    {
        var policy = new WatchRetryPolicy();
        policy.RecordFailure("acme", "/repo", T0);
        policy.Forget("acme", "/repo");
        policy.ShouldAttempt("acme", "/repo", T0).ShouldBeTrue();
        policy.IsStopped("acme", "/repo").ShouldBeFalse();
    }

    /// <summary>
    ///     The scheduler runs watch jobs on parallel tasks against one singleton policy; concurrent
    ///     failures on many watches must neither throw nor lose a watch's backoff.
    /// </summary>
    [Fact]
    public void RecordFailure_FromParallelJobs_KeepsEveryWatchsBackoff()
    {
        var policy = new WatchRetryPolicy();
        const int watches = 20_000;

        Parallel.For(0, watches, i => policy.RecordFailure("acme", $"/repo/{i}", T0));

        Enumerable.Range(0, watches).Count(i => policy.ShouldAttempt("acme", $"/repo/{i}", T0))
            .ShouldBe(0, "every failed watch must be backing off");
    }

    /// <summary>Five concurrent failures on one watch are five failures: the read-modify-write must not lose one.</summary>
    [Fact]
    public void RecordFailure_ConcurrentOnOneWatch_CountsEveryFailure()
    {
        for (var round = 0; round < 2_000; round++)
        {
            var policy = new WatchRetryPolicy();
            var path = $"/repo/{round}";

            Parallel.For(0, 5, _ => policy.RecordFailure("acme", path, T0));

            policy.IsStopped("acme", path).ShouldBeTrue($"round {round}: a failure was lost");
        }
    }
}
