using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot watch-config verb handlers; the only family with a ctor dependency (IWatchStore).</summary>
public sealed class WatchCommands(IWatchStore watchStore)
{
    // The ?? reads as redundant to the analyzer, but WatchCommandsTests proves the guard: the
    // CLI composes this by hand, outside DI, where a null can genuinely arrive.

    public async Task<int> SetEnabledAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var enabled = parseResult.GetValue<bool>("enabled");
        var key = target == "*" ? WatchConfigKeys.EnabledGlobal : WatchConfigKeys.EnabledProject(target);
        await store.SetSettingAsync(key, enabled ? "true" : "false", cancellationToken);

        if (target == "*" && enabled &&
            IngestScopeList.Parse(await store.GetSettingAsync(IngestScopeKeys.ScopeGlobal, cancellationToken)).Count == 0)
        {
            await streams.WriteErrorLineAsync(
                "ai-raccoon: warning — no ingest scope configured; add at least one scope with 'ai-raccoon ingest scope add '*' <path>'. Nothing can be ingested or watched until you do.");
        }

        await streams.WriteOutputLineAsync($"watch {(enabled ? "enabled" : "disabled")} for {target}");
        return 0;
    }

    public async Task<int> ScopeAddAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var path = parseResult.GetValue<string>("path")!;
        var key = target == "*" ? IngestScopeKeys.ScopeGlobal : IngestScopeKeys.ScopeProject(target);
        var current = IngestScopeList.Parse(await store.GetSettingAsync(key, cancellationToken));
        var updated = IngestScopeList.Add(current, path);
        await store.SetSettingAsync(key, IngestScopeList.ToJson(updated), cancellationToken);
        await streams.WriteOutputLineAsync($"added {Path.GetFullPath(path)} to ingest scope for {target}");
        return 0;
    }

    public async Task<int> ScopeRemoveAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var path = parseResult.GetValue<string>("path")!;
        var key = target == "*" ? IngestScopeKeys.ScopeGlobal : IngestScopeKeys.ScopeProject(target);
        var current = IngestScopeList.Parse(await store.GetSettingAsync(key, cancellationToken));
        var updated = IngestScopeList.Remove(current, path);
        if (updated.Count == 0)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }
        else
        {
            await store.SetSettingAsync(key, IngestScopeList.ToJson(updated), cancellationToken);
        }

        await streams.WriteOutputLineAsync($"removed {Path.GetFullPath(path)} from ingest scope for {target}");
        return 0;
    }

    public async Task<int> ScopeListAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var key = target == "*" ? IngestScopeKeys.ScopeGlobal : IngestScopeKeys.ScopeProject(target);
        foreach (var path in IngestScopeList.Parse(await store.GetSettingAsync(key, cancellationToken)))
        {
            await streams.WriteOutputLineAsync(path);
        }

        return 0;
    }

    public async Task<int> ConcurrencyAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        var value = parseResult.GetValue<int>("value");
        if (value is < 1 or > 16)
        {
            await streams.WriteErrorLineAsync($"ai-raccoon: invalid-value: concurrency {value} (expected 1..16)");
            return 1;
        }

        var key = target == "*" ? WatchConfigKeys.ConcurrencyGlobal : WatchConfigKeys.ConcurrencyProject(target);
        await store.SetSettingAsync(key, value.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await streams.WriteOutputLineAsync($"watch concurrency set to {value} for {target}");
        return 0;
    }

    public async Task<int> ListAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        // Two prefixes since the scope moved out of watch.*: enabled/concurrency stay watch-only,
        // the scope allowlist bounds all ingestion.
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prefix in (string[])["watch.", "ingest.scope."])
        {
            foreach (var row in await store.GetSettingsByPrefixAsync(prefix, cancellationToken))
            {
                rows[row.Key] = row.Value;
            }
        }

        var targets = new SortedSet<string>(StringComparer.Ordinal) { "global" };
        foreach (var key in rows.Keys)
        {
            targets.Add(key.StartsWith("watch.enabled.", StringComparison.Ordinal)
                ? key["watch.enabled.".Length..]
                : key.StartsWith("ingest.scope.", StringComparison.Ordinal)
                    ? key["ingest.scope.".Length..]
                    : key["watch.concurrency.".Length..]);
        }

        foreach (var target in targets)
        {
            // target "global" maps to the global keys by construction (Project("global") == Global).
            var config = WatchConfig.Resolve(target, key => rows.GetValueOrDefault(key));
            await streams.WriteOutputLineAsync(WatchListFormat.Render(target, config));
        }

        return 0;
    }

    public async Task<int> RegisteredAsync(ParseResult parseResult, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var filter = parseResult.GetValue<string?>("project-id");
        var watches = await watchStore.ListWatchesAsync(cancellationToken);
        var rows = watches
            .Where(w => filter is null || w.ProjectId == filter)
            .OrderBy(w => w.ProjectId, StringComparer.Ordinal)
            .ThenBy(w => w.Path, IngestPath.PathComparer)
            .ToArray();
        if (rows.Length == 0)
        {
            await streams.WriteOutputLineAsync("no registered watches");
            return 0;
        }

        foreach (var row in rows)
        {
            var registered = FormatTimestamp(row.CreatedAt);
            var lastChange = row.LastChangeTs == 0 ? "never" : FormatTimestamp(row.LastChangeTs);
            await streams.WriteOutputLineAsync($"project: {row.ProjectId}  path: {row.Path}  registered: {registered}  lastChange: {lastChange}");
        }

        return 0;
    }

    public async Task<int> RemoveAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams, CancellationToken cancellationToken)
    {
        var target = parseResult.GetValue<string>("target")!;
        string[] keys = target == "*"
            ? [WatchConfigKeys.EnabledGlobal, IngestScopeKeys.ScopeGlobal, WatchConfigKeys.ConcurrencyGlobal]
            : [WatchConfigKeys.EnabledProject(target), IngestScopeKeys.ScopeProject(target), WatchConfigKeys.ConcurrencyProject(target)];
        foreach (var key in keys)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await streams.WriteOutputLineAsync($"removed watch config for {target}");
        return 0;
    }

    private static string FormatTimestamp(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
