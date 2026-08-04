namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Runs digest jobs with per-project concurrency gates (resolved from watch.concurrency.*,
///     default 4, clamped 1-16) and round-robin admission across watches so one watch's flood
///     cannot starve others (feature rule 12).
/// </summary>
public sealed class WatchScheduler
{
    private const int DefaultConcurrency = 4;
    private const int MaxConcurrency = 16;

    private readonly Dictionary<string, SemaphoreSlim> _projectGates = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async Task RunBatchAsync(
        IReadOnlyList<WatchJob> jobs,
        IReadOnlyDictionary<string, int> concurrencyByProject,
        Func<WatchJob, CancellationToken, Task> runJob,
        CancellationToken cancellationToken = default)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        var ordered = OrderRoundRobin(jobs);
        var tasks = new List<Task>(jobs.Count);
        foreach (var job in ordered)
        {
            var gate = GateFor(job.Event.ProjectId, concurrencyByProject);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks.Add(Task.Run(() => RunGuardedAsync(job, gate, runJob, cancellationToken), cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Interleaves one job per watch per round (first-seen watch order).</summary>
    internal static IReadOnlyList<WatchJob> OrderRoundRobin(IReadOnlyList<WatchJob> jobs)
    {
        var queues = new List<Queue<WatchJob>>();
        var byWatch = new Dictionary<string, Queue<WatchJob>>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var key = WatchKey(job);
            if (!byWatch.TryGetValue(key, out var queue))
            {
                queue = new Queue<WatchJob>();
                byWatch[key] = queue;
                queues.Add(queue);
            }

            queue.Enqueue(job);
        }

        var result = new List<WatchJob>(jobs.Count);
        while (queues.Count > 0)
        {
            for (var i = 0; i < queues.Count; i++)
            {
                var queue = queues[i];
                result.Add(queue.Dequeue());
                if (queue.Count == 0)
                {
                    queues.RemoveAt(i);
                    i--;
                }
            }
        }

        return result;
    }

    private static string WatchKey(WatchJob job) => job.Event.ProjectId + "\u0000" + job.WatchPath;

    private static async Task RunGuardedAsync(WatchJob job, SemaphoreSlim gate,
        Func<WatchJob, CancellationToken, Task> runJob, CancellationToken cancellationToken)
    {
        try
        {
            await runJob(job, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private SemaphoreSlim GateFor(string projectId, IReadOnlyDictionary<string, int> concurrencyByProject)
    {
        lock (_gate)
        {
            if (_projectGates.TryGetValue(projectId, out var existing))
            {
                return existing;
            }

            var limit = Math.Clamp(concurrencyByProject.GetValueOrDefault(projectId, DefaultConcurrency), 1,
                MaxConcurrency);
            var gate = new SemaphoreSlim(limit, limit);
            _projectGates[projectId] = gate;
            return gate;
        }
    }
}
