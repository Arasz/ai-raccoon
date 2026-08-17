using AiRaccoon.Core.Memory.Filtering;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     Read path for noise_entries (ADR-0029/ADR-0039): without this, the "high-quality dataset of
///     true negatives" the ADR promised is write-only again. Takes the narrow
///     <see cref="INoiseSummaryStore" /> (ADR-0075 amendment) rather than the full
///     <see cref="INoiseEntryStore" /> — the CLI graph swaps it for a server-backed store; the
///     record/list/purge members of the full interface are server-side only and never belong on a
///     CLI command's own constructor.
/// </summary>
public sealed class NoiseEntriesCommands(INoiseSummaryStore noiseSummaryStore)
{
    public async Task<int> SummarizeAsync(StandardStreams streams, CancellationToken cancellationToken)
    {
        var summary = await noiseSummaryStore.SummarizeAsync(cancellationToken);

        await streams.WriteOutputLineAsync($"total: {summary.TotalCount}");
        foreach (var (policy, count) in summary.CountByPolicy.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            await streams.WriteOutputLineAsync($"  {policy}: {count}");
        }

        return 0;
    }
}
