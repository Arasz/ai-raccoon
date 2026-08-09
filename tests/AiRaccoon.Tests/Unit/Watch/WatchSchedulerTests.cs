using AiRaccoon.Infrastructure.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Per-project concurrency gates + round-robin admission across watches.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchSchedulerTests
{
    private static readonly string FloodWatch = "/flood";
    private static readonly string SmallWatch = "/small";

    private static WatchJob Job(string projectId, string watch, int index) => new(new WatchEvent(projectId, Path.Combine(watch, $"f{index}.md"), WatchEventKind.Changed), watch);

    private static IReadOnlyList<WatchJob> Jobs(string projectId, string watch, int count) => [.. Enumerable.Range(1, count).Select(i => Job(projectId, watch, i))];

    private static IReadOnlyDictionary<string, int> Concurrency(params (string Project, int Limit)[] entries) => entries.ToDictionary(e => e.Project, e => e.Limit);

    [Fact]
    public async Task RunBatch_TenJobsAcrossThreeWatches_AtMostFourConcurrent_AllComplete()
    {
        var scheduler = new WatchScheduler();
        var runner = new GatedRunner { Expected = 4 };
        var jobs = Jobs("acme", "/w1", 4)
            .Concat(Jobs("acme", "/w2", 3))
            .Concat(Jobs("acme", "/w3", 3))
            .ToArray();

        var batch = scheduler.RunBatchAsync(jobs, Concurrency(("acme", 4)), runner.Run,
            TestContext.Current.CancellationToken);
        await runner.AllExpectedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        runner.Started.ShouldBe(4);
        runner.MaxConcurrent.ShouldBe(4);
        runner.Release();
        await batch.WaitAsync(TestContext.Current.CancellationToken);

        runner.Completed.ShouldBe(10);
    }

    [Fact]
    public async Task RunBatch_ConcurrencyLimit_ComesFromPerProjectConfig()
    {
        var scheduler = new WatchScheduler();
        var runner = new GatedRunner { Expected = 2 };
        var jobs = Jobs("proj-a", "/w", 5);

        var batch = scheduler.RunBatchAsync(jobs, Concurrency(("proj-a", 2)), runner.Run,
            TestContext.Current.CancellationToken);
        await runner.AllExpectedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        runner.MaxConcurrent.ShouldBe(2);
        runner.Release();
        await batch.WaitAsync(TestContext.Current.CancellationToken);

        runner.Completed.ShouldBe(5);
    }

    [Fact]
    public async Task RunBatch_TwoProjects_GatesAreIndependent()
    {
        var scheduler = new WatchScheduler();
        var runner = new GatedRunner { Expected = 4 };
        var jobs = Jobs("proj-a", "/w", 3).Concat(Jobs("proj-b", "/w", 3)).ToArray();

        var batch = scheduler.RunBatchAsync(jobs, Concurrency(("proj-a", 2), ("proj-b", 2)), runner.Run,
            TestContext.Current.CancellationToken);
        await runner.AllExpectedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        runner.Started.ShouldBe(4);
        runner.MaxConcurrent.ShouldBe(4);
        runner.Release();
        await batch.WaitAsync(TestContext.Current.CancellationToken);

        runner.Completed.ShouldBe(6);
    }

    [Fact]
    public async Task RunBatch_FloodWatchDoesNotStarveSmallWatch_RoundRobinAdmission()
    {
        var scheduler = new WatchScheduler();
        var runner = new RecordingRunner();
        var jobs = Jobs("acme", FloodWatch, 10).Concat(Jobs("acme", SmallWatch, 1)).ToArray();

        // Concurrency 1 serializes admission, making the round-robin order observable.
        await scheduler.RunBatchAsync(jobs, Concurrency(("acme", 1)), runner.Run,
            TestContext.Current.CancellationToken);

        runner.Order.Count.ShouldBe(11);
        runner.Order[1].ShouldBe(Path.Combine(SmallWatch, "f1.md"));
        runner.Order.Count(p => p.Contains(SmallWatch, StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task RunBatch_LimitRaisedBetweenBatches_NextBatchUsesTheNewLimit()
    {
        var scheduler = new WatchScheduler();
        var first = new GatedRunner { Expected = 1 };
        var jobs = Jobs("acme", "/w", 2);

        // Batch 1 at limit 1: only the first job is admitted until released.
        var firstBatch = scheduler.RunBatchAsync(jobs, Concurrency(("acme", 1)), first.Run,
            TestContext.Current.CancellationToken);
        await first.AllExpectedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        first.Started.ShouldBe(1);
        first.Release();
        await firstBatch.WaitAsync(TestContext.Current.CancellationToken);
        first.Completed.ShouldBe(2);

        // Batch 2 after the limit was raised to 2: both jobs admit WITHOUT any release —
        // a gate cached from the first batch would still serialize them.
        var second = new GatedRunner { Expected = 2 };
        var secondBatch = scheduler.RunBatchAsync(jobs, Concurrency(("acme", 2)), second.Run,
            TestContext.Current.CancellationToken);
        await second.AllExpectedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        second.Started.ShouldBe(2, "the raised limit must apply to the next batch");
        second.Release();
        await secondBatch.WaitAsync(TestContext.Current.CancellationToken);
        second.Completed.ShouldBe(2);
    }

    [Fact]
    public async Task RunBatch_EmptyBatch_RunsNothing()
    {
        var scheduler = new WatchScheduler();
        var runner = new RecordingRunner();
        await scheduler.RunBatchAsync([], new Dictionary<string, int>(), runner.Run,
            TestContext.Current.CancellationToken);
        runner.Order.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunBatch_ClampsConfiguredLimit_ToTheOneToSixteenRange()
    {
        var scheduler = new WatchScheduler();
        var runner = new GatedRunner { Expected = 16 };
        var jobs = Jobs("acme", "/w", 20);

        var batch = scheduler.RunBatchAsync(jobs, Concurrency(("acme", 99)), runner.Run,
            TestContext.Current.CancellationToken);
        await runner.AllExpectedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        runner.Started.ShouldBe(16);
        runner.Release();
        await batch.WaitAsync(TestContext.Current.CancellationToken);

        runner.Completed.ShouldBe(20);
    }

    [Fact]
    public async Task RunBatch_ZeroLimit_ClampsToOne()
    {
        var scheduler = new WatchScheduler();
        var runner = new GatedRunner { Expected = 1 };
        var jobs = Jobs("acme", "/w", 3);

        var batch = scheduler.RunBatchAsync(jobs, Concurrency(("acme", 0)), runner.Run,
            TestContext.Current.CancellationToken);
        await runner.AllExpectedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        runner.Started.ShouldBe(1);
        runner.Release();
        await batch.WaitAsync(TestContext.Current.CancellationToken);

        runner.Completed.ShouldBe(3);
    }

    /// <summary>Holds admitted jobs until Release, recording concurrency and start order.</summary>
    private sealed class GatedRunner
    {
        private readonly object _lock = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _current;

        public int Expected { get; init; }

        public int Started { get; private set; }

        public int Completed { get; private set; }

        public int MaxConcurrent { get; private set; }

        public TaskCompletionSource AllExpectedStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Run(WatchJob job, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Started++;
                _current++;
                MaxConcurrent = Math.Max(MaxConcurrent, _current);
                if (Started == Expected)
                {
                    AllExpectedStarted.TrySetResult();
                }
            }

            await _release.Task.WaitAsync(cancellationToken);
            lock (_lock)
            {
                Completed++;
                _current--;
            }
        }

        public void Release() => _release.TrySetResult();
    }

    /// <summary>Completes immediately; admission order is the recorded order (use with concurrency 1).</summary>
    private sealed class RecordingRunner
    {
        public List<string> Order { get; } = [];

        public Task Run(WatchJob job, CancellationToken cancellationToken)
        {
            lock (Order)
            {
                Order.Add(job.Event.Path);
            }

            return Task.CompletedTask;
        }
    }
}
