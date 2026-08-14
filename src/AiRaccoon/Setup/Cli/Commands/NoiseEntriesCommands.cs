using AiRaccoon.Core.Memory.Filtering;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     Read path for noise_entries (ADR-0029/ADR-0039): without this, the "high-quality dataset of
///     true negatives" the ADR promised is write-only again.
/// </summary>
public sealed class NoiseEntriesCommands(INoiseEntryStore noiseEntryStore)
{
    public async Task<int> SummarizeAsync(StandardStreams streams, CancellationToken cancellationToken)
    {
        var summary = await noiseEntryStore.SummarizeAsync(cancellationToken);

        await streams.WriteOutputLineAsync($"total: {summary.TotalCount}");
        foreach (var (policy, count) in summary.CountByPolicy.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            await streams.WriteOutputLineAsync($"  {policy}: {count}");
        }

        return 0;
    }
}
