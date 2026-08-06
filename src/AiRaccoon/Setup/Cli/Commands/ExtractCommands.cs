using System.CommandLine;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot extract-config verb handlers: enable, mode, list — the CLI-only channel for the background extraction service.</summary>
public sealed class ExtractCommands : IExtractCommands
{
    public async Task<int> SetEnabledAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var enabled = parseResult.GetValue<bool>("enabled");
        await store.SetSettingAsync(ExtractionConfigKeys.EnabledGlobal, enabled ? "true" : "false",
            cancellationToken);
        await stdout.WriteLineAsync($"shared extraction {(enabled ? "enabled" : "disabled")}");
        if (enabled)
        {
            var mode = ExtractionConfigKeys.ParseMode(
                await store.GetSettingAsync(ExtractionConfigKeys.ModeGlobal, cancellationToken));
            await stdout.WriteLineAsync($"mode: {mode.ToString().ToLowerInvariant()} " +
                (mode == ExtractMode.Promote
                    ? "(promote shares candidates with ALL projects — review the mode before relying on it)"
                    : "(propose only logs candidates; nothing is shared)"));
        }

        return 0;
    }

    public async Task<int> SetModeAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken)
    {
        var mode = parseResult.GetValue<string>("mode");
        if (mode is not ("propose" or "promote"))
        {
            await stderr.WriteLineAsync("ai-raccoon: mode must be 'propose' or 'promote'");
            return 1;
        }

        await store.SetSettingAsync(ExtractionConfigKeys.ModeGlobal, mode, cancellationToken);
        await stdout.WriteLineAsync(
            $"extraction mode: {mode} — {(mode == "promote" ? "shares candidates with ALL projects" : "logs candidates only, never shares")}");
        return 0;
    }

    public async Task<int> SetIntervalAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken)
    {
        var minutes = parseResult.GetValue<string>("minutes");
        if (!int.TryParse(minutes, out var parsed) || parsed <= 0)
        {
            await stderr.WriteLineAsync("ai-raccoon: interval must be a positive number of minutes");
            return 1;
        }

        await store.SetSettingAsync(ExtractionConfigKeys.IntervalMinutesGlobal, parsed.ToString(),
            cancellationToken);
        await stdout.WriteLineAsync($"extraction interval: {parsed} min");
        return 0;
    }

    public async Task<int> SetCapacityAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
        TextWriter stderr, CancellationToken cancellationToken)
    {
        var capacity = parseResult.GetValue<string>("capacity");
        if (!int.TryParse(capacity, out var parsed) || parsed <= 0)
        {
            await stderr.WriteLineAsync("ai-raccoon: capacity must be a positive number of queued candidates");
            return 1;
        }

        await store.SetSettingAsync(ExtractionConfigKeys.QueueCapacityGlobal, parsed.ToString(),
            cancellationToken);
        await stdout.WriteLineAsync($"propose-tier capacity: {parsed} candidates");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, TextWriter stdout, CancellationToken cancellationToken)
    {
        var enabled = ExtractionConfigKeys.ParseEnabled(
            await store.GetSettingAsync(ExtractionConfigKeys.EnabledGlobal, cancellationToken));
        var mode = ExtractionConfigKeys.ParseMode(
            await store.GetSettingAsync(ExtractionConfigKeys.ModeGlobal, cancellationToken));
        var interval = ExtractionConfigKeys.ParseIntervalMinutes(
            await store.GetSettingAsync(ExtractionConfigKeys.IntervalMinutesGlobal, cancellationToken));
        var capacity = ExtractionConfigKeys.ParseQueueCapacity(
            await store.GetSettingAsync(ExtractionConfigKeys.QueueCapacityGlobal, cancellationToken));
        await stdout.WriteLineAsync(
            $"enabled: {enabled}  mode: {mode.ToString().ToLowerInvariant()}  interval: {interval} min  " +
            $"queue-capacity: {capacity}");
        return 0;
    }

    public async Task<int> ExcludeAddAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
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

        await stdout.WriteLineAsync($"excluded source prefixes: {string.Join(", ", prefixes)}");
        return 0;
    }

    public async Task<int> ExcludeRemoveAsync(ParseResult parseResult, IMemoryStore store, TextWriter stdout,
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

        await stdout.WriteLineAsync($"excluded source prefixes: {string.Join(", ", prefixes)}");
        return 0;
    }

    public async Task<int> ExcludeListAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var prefixes = ExtractionConfigKeys.ParseExcludePrefixes(
            await store.GetSettingAsync(ExtractionConfigKeys.ExcludePrefixesGlobal, cancellationToken));
        if (prefixes.Count == 0)
        {
            await stdout.WriteLineAsync("excluded source prefixes: none");
            return 0;
        }

        foreach (var prefix in prefixes)
        {
            await stdout.WriteLineAsync(prefix);
        }

        return 0;
    }
}
