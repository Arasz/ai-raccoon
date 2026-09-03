using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot extract-config verb handlers: enable, mode, list, exclude — the CLI-only channel
///     for the background extraction service. `prune` is a thin client over
///     <see cref="IPromotionQueuePruneStore" /> (ADR-0075 amendment) — the report is scanned
///     server-side and never opens the bank from the CLI process; --apply commits a request the
///     server applies, rather than deleting here.
/// </summary>
public sealed class ExtractCommands(IPromotionQueuePruneStore promotionQueuePrune)
{
    public async Task<int> SetEnabledAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var enabled = parseResult.GetValue<bool>("enabled");
        await store.SetSettingAsync(ExtractionConfigKeys.EnabledGlobal, enabled ? "true" : "false",
            cancellationToken);
        await streams.WriteOutputLineAsync($"shared extraction {(enabled ? "enabled" : "disabled")}");
        if (!enabled)
        {
            return 0;
        }

        var mode = ExtractionConfigKeys.ParseMode(
            await store.GetSettingAsync(ExtractionConfigKeys.ModeGlobal, cancellationToken));
        await streams.WriteOutputLineAsync($"mode: {mode.ToString().ToLowerInvariant()} " +
                                           (mode == ExtractMode.Promote
                                               ? "(promote shares candidates with ALL projects — review the mode before relying on it)"
                                               : "(propose only logs candidates; nothing is shared)"));

        return 0;
    }

    public async Task<int> SetModeAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var mode = parseResult.GetValue<string>("mode");
        if (mode is not ("propose" or "promote"))
        {
            await streams.WriteErrorLineAsync("ai-raccoon: mode must be 'propose' or 'promote'");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(ExtractionConfigKeys.ModeGlobal, mode, cancellationToken);
        await streams.WriteOutputLineAsync(
            $"extraction mode: {mode} — {(mode == "promote" ? "shares candidates with ALL projects" : "logs candidates only, never shares")}");
        return 0;
    }

    public async Task<int> SetIntervalAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var minutes = parseResult.GetValue<string>("minutes");
        if (!int.TryParse(minutes, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: interval must be a positive number of minutes");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(ExtractionConfigKeys.IntervalMinutesGlobal, parsed.ToString(),
            cancellationToken);
        await streams.WriteOutputLineAsync($"extraction interval: {parsed} min");
        return 0;
    }

    public async Task<int> SetCapacityAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var capacity = parseResult.GetValue<string>("capacity");
        if (!int.TryParse(capacity, out var parsed) || parsed <= 0)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: capacity must be a positive number of queued candidates");
            return ExitCode.InvalidArgument;
        }

        await store.SetSettingAsync(ExtractionConfigKeys.QueueCapacityGlobal, parsed.ToString(),
            cancellationToken);
        await streams.WriteOutputLineAsync($"propose-tier capacity: {parsed} candidates");
        return 0;
    }

    public async Task<int> SetAutoPromoteThresholdAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("threshold");
        if (raw is not null && raw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            await store.DeleteSettingAsync(ExtractionConfigKeys.AutoPromoteThresholdGlobal, cancellationToken);
            await streams.WriteOutputLineAsync("auto-promote-threshold: off (auto-promotion disabled)");
            return 0;
        }

        var parsed = ExtractionConfigKeys.ParseAutoPromoteThreshold(raw);
        if (!parsed.HasValue)
        {
            await streams.WriteErrorLineAsync("ai-raccoon: threshold must be a score 0..4 or 'off'");
            return ExitCode.InvalidArgument;
        }

        var formatted = ExtractionConfigKeys.FormatAutoPromoteThreshold(parsed);
        await store.SetSettingAsync(ExtractionConfigKeys.AutoPromoteThresholdGlobal, formatted, cancellationToken);
        await streams.WriteOutputLineAsync(
            $"auto-promote-threshold: {formatted} " +
            "(in promote mode, candidates scoring at or above share automatically; nothing below is queued)");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, StandardStreams streams, CancellationToken cancellationToken)
    {
        var enabled = ExtractionConfigKeys.ParseEnabled(
            await store.GetSettingAsync(ExtractionConfigKeys.EnabledGlobal, cancellationToken));
        var mode = ExtractionConfigKeys.ParseMode(
            await store.GetSettingAsync(ExtractionConfigKeys.ModeGlobal, cancellationToken));
        var interval = ExtractionConfigKeys.ParseIntervalMinutes(
            await store.GetSettingAsync(ExtractionConfigKeys.IntervalMinutesGlobal, cancellationToken));
        var capacity = ExtractionConfigKeys.ParseQueueCapacity(
            await store.GetSettingAsync(ExtractionConfigKeys.QueueCapacityGlobal, cancellationToken));
        var threshold = ExtractionConfigKeys.FormatAutoPromoteThreshold(
            ExtractionConfigKeys.ParseAutoPromoteThreshold(
                await store.GetSettingAsync(ExtractionConfigKeys.AutoPromoteThresholdGlobal, cancellationToken)));
        await streams.WriteOutputLineAsync(
            $"enabled: {enabled}  mode: {mode.ToString().ToLowerInvariant()}  interval: {interval} min  " +
            $"queue-capacity: {capacity}  auto-promote-threshold: {threshold}");
        return 0;
    }

    public async Task<int> ExcludeAddAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var prefix = parseResult.GetValue<string>("prefix");
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var prefixes = ExtractionConfigKeys.ParseExcludePrefixes(
                await store.GetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, cancellationToken))
            .ToList();
        if (!prefixes.Contains(prefix, StringComparer.Ordinal))
        {
            prefixes.Add(prefix);
            await store.SetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, string.Join(",", prefixes),
                cancellationToken);
        }

        await streams.WriteOutputLineAsync($"excluded source prefixes: {string.Join(", ", prefixes)}");
        return 0;
    }

    public async Task<int> ExcludeRemoveAsync(ParseResult parseResult, IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var prefix = parseResult.GetValue<string>("prefix");
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var prefixes = ExtractionConfigKeys.ParseExcludePrefixes(
                await store.GetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, cancellationToken))
            .ToList();
        if (prefixes.Remove(prefix))
        {
            if (prefixes.Count == 0)
            {
                await store.DeleteSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, cancellationToken);
            }
            else
            {
                await store.SetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, string.Join(",", prefixes),
                    cancellationToken);
            }
        }

        await streams.WriteOutputLineAsync($"excluded source prefixes: {string.Join(", ", prefixes)}");
        return 0;
    }

    public async Task<int> ExcludeListAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var prefixes = ExtractionConfigKeys.ParseExcludePrefixes(
            await store.GetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, cancellationToken));
        if (prefixes.Count == 0)
        {
            await streams.WriteOutputLineAsync("excluded source prefixes: none");
            return 0;
        }

        foreach (var prefix in prefixes)
        {
            await streams.WriteOutputLineAsync(prefix);
        }

        return 0;
    }

    public async Task<int> PruneAsync(ParseResult parseResult, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var apply = parseResult.GetValue<bool>("--apply");
        var report = await promotionQueuePrune.ReportPruneOrphansAsync(cancellationToken);

        if (report.TotalOrphans == 0)
        {
            await streams.WriteOutputLineAsync("promotion queue: no orphaned candidates found");
            return 0;
        }

        var verb = apply ? "queued for the server to remove" : "found (dry run; pass --apply to remove)";
        foreach (var (projectId, count) in report.PerProject.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            await streams.WriteOutputLineAsync($"{projectId}: {count} orphaned candidate(s) {verb}");
        }

        await streams.WriteOutputLineAsync($"total: {report.TotalOrphans} orphaned candidate(s) {verb}");

        if (apply)
        {
            await promotionQueuePrune.RequestPruneOrphansAsync(cancellationToken);
            await streams.WriteOutputLineAsync(
                "promotion queue: request committed; the server removes the orphaned candidates on its next maintenance poll (~15s).");
        }

        return 0;
    }
}
