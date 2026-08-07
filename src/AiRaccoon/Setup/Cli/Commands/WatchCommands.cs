using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot watch-config verb handlers; the only family with a ctor dependency (IWatchStore).</summary>
public sealed class WatchCommands(IWatchStore watchStore)
{
    private readonly IWatchStore _watchStore = watchStore ?? ThrowHelper.ThrowArgumentNullException<IWatchStore>(nameof(watchStore));
    public async Task<int> SetEnabledAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var enabled = parseResult.GetValue<bool>("enabled");
        var key = target == "*" ? WatchConfigKeys.EnabledGlobal : WatchConfigKeys.EnabledProject(target);
        await store.SetSettingAsync(key, enabled ? "true" : "false", cancellationToken);

        if (target == "*" && enabled &&
            WatchScopeList.Parse(await store.GetSettingAsync(WatchConfigKeys.ScopeGlobal, cancellationToken)).Count == 0)
        {
            await stderr.WriteLineAsync(
                "ai-raccoon: warning — no watch scopes configured; add at least one scope with 'ai-raccoon watch scope add '*' <path>'");
        }

        await stdout.WriteLineAsync($"watch {(enabled ? "enabled" : "disabled")} for {target}");
        return 0;
    }

    public async Task<int> ScopeAddAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var path = parseResult.GetValue<string>("path")!;
        var key = target == "*" ? WatchConfigKeys.ScopeGlobal : WatchConfigKeys.ScopeProject(target);
        var current = WatchScopeList.Parse(await store.GetSettingAsync(key, cancellationToken));
        var updated = WatchScopeList.Add(current, path);
        await store.SetSettingAsync(key, WatchScopeList.ToJson(updated), cancellationToken);
        await stdout.WriteLineAsync($"added {Path.GetFullPath(path)} to watch scope for {target}");
        return 0;
    }

    public async Task<int> ScopeRemoveAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var path = parseResult.GetValue<string>("path")!;
        var key = target == "*" ? WatchConfigKeys.ScopeGlobal : WatchConfigKeys.ScopeProject(target);
        var current = WatchScopeList.Parse(await store.GetSettingAsync(key, cancellationToken));
        var updated = WatchScopeList.Remove(current, path);
        if (updated.Count == 0)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }
        else
        {
            await store.SetSettingAsync(key, WatchScopeList.ToJson(updated), cancellationToken);
        }

        await stdout.WriteLineAsync($"removed {Path.GetFullPath(path)} from watch scope for {target}");
        return 0;
    }

    public async Task<int> ScopeListAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var key = target == "*" ? WatchConfigKeys.ScopeGlobal : WatchConfigKeys.ScopeProject(target);
        foreach (var path in WatchScopeList.Parse(await store.GetSettingAsync(key, cancellationToken)))
        {
            await stdout.WriteLineAsync(path);
        }

        return 0;
    }

    public async Task<int> ConcurrencyAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var value = parseResult.GetValue<int>("value");
        if (value is < 1 or > 16)
        {
            await stderr.WriteLineAsync($"ai-raccoon: invalid-value: concurrency {value} (expected 1..16)");
            return 1;
        }

        var key = target == "*" ? WatchConfigKeys.ConcurrencyGlobal : WatchConfigKeys.ConcurrencyProject(target);
        await store.SetSettingAsync(key, value.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await stdout.WriteLineAsync($"watch concurrency set to {value} for {target}");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("watch.", cancellationToken);
        var targets = new SortedSet<string>(StringComparer.Ordinal) { "global" };
        foreach (var key in rows.Keys)
        {
            targets.Add(key.StartsWith("watch.enabled.", StringComparison.Ordinal)
                ? key["watch.enabled.".Length..]
                : key.StartsWith("watch.scope.", StringComparison.Ordinal)
                    ? key["watch.scope.".Length..]
                    : key["watch.concurrency.".Length..]);
        }

        foreach (var target in targets)
        {
            // target "global" maps to the global keys by construction (Project("global") == Global).
            var config = WatchConfig.Resolve(target, key => rows.GetValueOrDefault(key));
            await stdout.WriteLineAsync(WatchListFormat.Render(target, config));
        }

        return 0;
    }

    public async Task<int> RegisteredAsync(ParseResult parseResult, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var filter = parseResult.GetValue<string?>("project-id");
        var watches = await _watchStore.ListWatchesAsync(cancellationToken);
        var rows = watches
            .Where(w => filter is null || w.ProjectId == filter)
            .OrderBy(w => w.ProjectId, StringComparer.Ordinal)
            .ThenBy(w => w.Path, WatchPath.PathComparer)
            .ToArray();
        if (rows.Length == 0)
        {
            await stdout.WriteLineAsync("no registered watches");
            return 0;
        }

        foreach (var row in rows)
        {
            var registered = FormatTimestamp(row.CreatedAt);
            var lastChange = row.LastChangeTs == 0 ? "never" : FormatTimestamp(row.LastChangeTs);
            await stdout.WriteLineAsync($"project: {row.ProjectId}  path: {row.Path}  registered: {registered}  lastChange: {lastChange}");
        }

        return 0;
    }

    public async Task<int> RemoveAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        string[] keys = target == "*"
            ? [WatchConfigKeys.EnabledGlobal, WatchConfigKeys.ScopeGlobal, WatchConfigKeys.ConcurrencyGlobal]
            : [WatchConfigKeys.EnabledProject(target), WatchConfigKeys.ScopeProject(target), WatchConfigKeys.ConcurrencyProject(target)];
        foreach (var key in keys)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await stdout.WriteLineAsync($"removed watch config for {target}");
        return 0;
    }

    private static string FormatTimestamp(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
