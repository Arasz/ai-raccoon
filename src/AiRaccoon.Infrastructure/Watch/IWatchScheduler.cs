namespace AiRaccoon.Infrastructure.Watch;

public interface IWatchScheduler
{
    Task RunBatchAsync(
        IReadOnlyList<WatchJob> jobs,
        IReadOnlyDictionary<string, int> concurrencyByProject,
        Func<WatchJob, CancellationToken, Task> runJob,
        CancellationToken cancellationToken = default);
}
