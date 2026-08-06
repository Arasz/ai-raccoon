using System.CommandLine;
using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot config commands (the single runtime config channel): each verb opens the
///     bank through the given store, applies settings-table writes, prints a result, and
///     returns the process exit code. User-run commands get no access-tier checks; errors
///     go to stderr, results to stdout.
/// </summary>
internal static class ConfigCommands
{
    public static async Task<int> RunAsync(string[] commandPath, ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        ISettingsCommands? settings = null, ISyncCommands? sync = null,
        IEncryptionCommands? encryptionCommands = null, IWatchStore? watchStore = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return commandPath switch
            {
                ["access", "default", "set"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).AccessDefaultSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["access", "default", "show"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).AccessDefaultShowAsync(store, stdout, cancellationToken),
                ["access", "set"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).AccessSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["access", "unset"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).AccessUnsetAsync(parseResult, store, stdout, cancellationToken),
                ["access", "list"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).AccessListAsync(store, stdout, cancellationToken),
                ["model", "set", "local"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).ModelSetLocalAsync(parseResult, store, stdout, cancellationToken),
                ["model", "set", "openai"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).ModelSetOpenAiAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["model", "reset"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).ModelResetAsync(store, stdout, cancellationToken),
                ["model", "show"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).ModelShowAsync(store, stdout, cancellationToken),
                ["retrieval", "alpha", "set"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).RetrievalAlphaSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["retrieval", "alpha", "show"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).RetrievalAlphaShowAsync(store, stdout, cancellationToken),
                ["sweep", "threshold", "set"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).SweepThresholdSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["sweep", "show"] => await (settings ?? ThrowHelper.ThrowArgumentNullException<ISettingsCommands>(nameof(settings))).SweepShowAsync(store, stdout, cancellationToken),
                ["sync", "add", "s3"] => await (sync ?? ThrowHelper.ThrowArgumentNullException<ISyncCommands>(nameof(sync))).AddS3Async(parseResult, store, stdout, stderr, stdin, cancellationToken),
                ["sync", "add", "azure"] => await (sync ?? ThrowHelper.ThrowArgumentNullException<ISyncCommands>(nameof(sync))).AddAzureAsync(parseResult, store, stdout, stderr, stdin, cancellationToken),
                ["sync", "remove"] => await (sync ?? ThrowHelper.ThrowArgumentNullException<ISyncCommands>(nameof(sync))).RemoveAsync(store, stdout, cancellationToken),
                ["sync", "show"] => await (sync ?? ThrowHelper.ThrowArgumentNullException<ISyncCommands>(nameof(sync))).ShowAsync(store, stdout, cancellationToken),
                ["watch", "enable"] or ["watch", "disable"] => await WatchSetEnabledAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["watch", "scope", "add"] => await WatchScopeAddAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "scope", "remove"] => await WatchScopeRemoveAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "scope", "list"] => await WatchScopeListAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "concurrency"] => await WatchConcurrencyAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["watch", "list"] => await WatchListAsync(store, stdout, cancellationToken),
                ["watch", "registered"] => await WatchRegisteredAsync(parseResult, watchStore, stdout, cancellationToken),
                ["watch", "remove"] => await WatchRemoveAsync(parseResult, store, stdout, cancellationToken),
                ["encryption", "bitwarden"] => await (encryptionCommands ?? ThrowHelper.ThrowArgumentNullException<IEncryptionCommands>(nameof(encryptionCommands))).BitwardenAsync(parseResult, store, stdout, stderr, stdin, cancellationToken),
                ["encryption", "show"] => await (encryptionCommands ?? ThrowHelper.ThrowArgumentNullException<IEncryptionCommands>(nameof(encryptionCommands))).ShowAsync(store, stdout, cancellationToken),
                ["encryption", "unset"] => await (encryptionCommands ?? ThrowHelper.ThrowArgumentNullException<IEncryptionCommands>(nameof(encryptionCommands))).UnsetAsync(store, stdout, stderr, cancellationToken),
                _ => throw new InvalidOperationException($"unhandled command: {string.Join(' ', commandPath)}")
            };
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }
    }

    // ── watch ──

    private static async Task<int> WatchSetEnabledAsync(ParseResult parseResult, IMemoryStore store,
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

    private static async Task<int> WatchScopeAddAsync(ParseResult parseResult, IMemoryStore store,
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

    private static async Task<int> WatchScopeRemoveAsync(ParseResult parseResult, IMemoryStore store,
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

    private static async Task<int> WatchScopeListAsync(ParseResult parseResult, IMemoryStore store,
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

    private static async Task<int> WatchConcurrencyAsync(ParseResult parseResult, IMemoryStore store,
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

    private static async Task<int> WatchListAsync(IMemoryStore store, TextWriter stdout,
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

    private static async Task<int> WatchRegisteredAsync(ParseResult parseResult, IWatchStore? watchStore,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        Guard.IsNotNull(watchStore);
        var filter = parseResult.GetValue<string?>("project-id");
        var watches = await watchStore.ListWatchesAsync(cancellationToken);
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

    private static string FormatTimestamp(long unixSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static async Task<int> WatchRemoveAsync(ParseResult parseResult, IMemoryStore store,
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

}
