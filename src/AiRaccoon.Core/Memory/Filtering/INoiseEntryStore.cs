namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>One rejected write, as recorded in noise_entries.</summary>
public sealed record NoiseEntry(
    long Id,
    string RequestContent,
    string ProjectId,
    string? SourceFile,
    string DetectedByPolicy,
    long ExpiresAt,
    long CreatedAt);

/// <summary>Aggregate counts over noise_entries — the read path ADR-0029's original store never had.</summary>
public sealed record NoiseEntrySummary(
    int TotalCount,
    IReadOnlyDictionary<string, int> CountByPolicy);

/// <summary>
///     Persistence for noise_entries (ADR-0029): the training-data source for a future noise
///     learner (ADR-0039) — every write a policy rejects, kept until its retention TTL purges it.
/// </summary>
public interface INoiseEntryStore
{
    Task RecordAsync(MemoryWriteRequest request, string policyName, long expiresAtUnixSeconds,
        long nowUnixSeconds, CancellationToken cancellationToken = default);

    Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NoiseEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes every row whose expires_at has passed <paramref name="nowUnixSeconds" />; returns the count removed.</summary>
    Task<int> PurgeExpiredAsync(long nowUnixSeconds, CancellationToken cancellationToken = default);
}

/// <summary>
///     Null Object default for <see cref="SqliteMemoryStore" />'s legacy (pre-noise-entries)
///     constructor — never records anything. Not a nullable injected parameter: a genuinely
///     functioning, always-non-null implementation that does nothing. Also implements
///     <see cref="INoiseSummaryStore" /> so the same instance serves as the null object wherever a
///     test needs one for the CLI's `noise entries` command without a real bank.
/// </summary>
public sealed class NoOpNoiseEntryStore : INoiseEntryStore, INoiseSummaryStore
{
    public static readonly NoOpNoiseEntryStore Instance = new();

    private NoOpNoiseEntryStore()
    {
    }

    public Task RecordAsync(MemoryWriteRequest request, string policyName, long expiresAtUnixSeconds,
        long nowUnixSeconds, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<NoiseEntrySummary> SummarizeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new NoiseEntrySummary(0, new Dictionary<string, int>(StringComparer.Ordinal)));

    public Task<IReadOnlyList<NoiseEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<NoiseEntry>>([]);

    public Task<int> PurgeExpiredAsync(long nowUnixSeconds, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
