using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>A burst of watch changes must cost the reconcile loop at most one extra pass (ADR-0152).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchReconcileSignalTests
{
    [Fact]
    public async Task WaitAsync_AfterARequest_Completes()
    {
        var signal = new WatchReconcileSignal();
        signal.Request();

        await signal.WaitAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Request_ManyTimes_LeavesExactlyOnePendingWake()
    {
        var signal = new WatchReconcileSignal();
        for (var i = 0; i < 100; i++)
        {
            signal.Request();
        }

        await signal.WaitAsync(TestContext.Current.CancellationToken);

        using var second = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        second.CancelAfter(TimeSpan.FromMilliseconds(150));
        await Should.ThrowAsync<OperationCanceledException>(async () => await signal.WaitAsync(second.Token));
        signal.RequestCount.ShouldBe(100);
    }

    [Fact]
    public async Task WaitAsync_NoRequest_WaitsUntilCancelled()
    {
        var signal = new WatchReconcileSignal();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(async () => await signal.WaitAsync(cts.Token));
    }
}
