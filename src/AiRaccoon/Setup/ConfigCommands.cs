using System.CommandLine;
using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sync;

namespace AiRaccoon.Setup;

/// <summary>
///     One-shot config commands (the single runtime config channel): each verb opens the
///     bank through the given store, applies settings-table writes, prints a result, and
///     returns the process exit code. User-run commands get no access-tier checks; errors
///     go to stderr, results to stdout.
/// </summary>
internal static class ConfigCommands
{
    public static async Task<int> RunAsync(string[] commandPath, ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin, CancellationToken cancellationToken = default)
    {
        try
        {
            return commandPath switch
            {
                ["access", "default", "set"] => await AccessDefaultSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["access", "default", "show"] => await AccessDefaultShowAsync(store, stdout, cancellationToken),
                ["access", "set"] => await AccessSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["access", "unset"] => await AccessUnsetAsync(parseResult, store, stdout, cancellationToken),
                ["access", "list"] => await AccessListAsync(store, stdout, cancellationToken),
                ["model", "set", "local"] => await ModelSetLocalAsync(parseResult, store, stdout, cancellationToken),
                ["model", "set", "openai"] => await ModelSetOpenAiAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["model", "reset"] => await ModelResetAsync(store, stdout, cancellationToken),
                ["model", "show"] => await ModelShowAsync(store, stdout, cancellationToken),
                ["retrieval", "alpha", "set"] => await RetrievalAlphaSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["retrieval", "alpha", "show"] => await RetrievalAlphaShowAsync(store, stdout, cancellationToken),
                ["sweep", "threshold", "set"] => await SweepThresholdSetAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["sweep", "show"] => await SweepShowAsync(store, stdout, cancellationToken),
                ["sync", "add", "s3"] => await SyncAddS3Async(parseResult, store, stdout, stderr, stdin, cancellationToken),
                ["sync", "add", "azure"] => await SyncAddAzureAsync(parseResult, store, stdout, stderr, stdin, cancellationToken),
                ["sync", "remove"] => await SyncRemoveAsync(store, stdout, cancellationToken),
                ["sync", "show"] => await SyncShowAsync(store, stdout, cancellationToken),
                ["watch", "enable"] or ["watch", "disable"] => await WatchSetEnabledAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["watch", "scope", "add"] => await WatchScopeAddAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "scope", "remove"] => await WatchScopeRemoveAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "scope", "list"] => await WatchScopeListAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "concurrency"] => await WatchConcurrencyAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["watch", "list"] => await WatchListAsync(store, stdout, cancellationToken),
                _ => throw new InvalidOperationException($"unhandled command: {string.Join(' ', commandPath)}")
            };
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"ai-raccoon: {ex.Message}");
            return 1;
        }
    }

    // ── access ──

    private static async Task<int> AccessDefaultSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var mode = parseResult.GetValue<string>("mode");
        if (AccessModePolicy.Parse(mode) is not { } parsed)
        {
            await stderr.WriteLineAsync($"ai-raccoon: invalid access mode '{mode}' (expected ro, rw or full)");
            return 1;
        }

        await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(parsed), cancellationToken);
        await stdout.WriteLineAsync($"access default set to {AccessModePolicy.Serialize(parsed)}");
        return 0;
    }

    private static async Task<int> AccessDefaultShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var raw = await store.GetSettingAsync(AccessModePolicy.GlobalSettingKey, cancellationToken);
        await stdout.WriteLineAsync(AccessModePolicy.Serialize(AccessModePolicy.Parse(raw) ?? AccessMode.Rw));
        return 0;
    }

    private static async Task<int> AccessSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("project-id")!;
        var mode = parseResult.GetValue<string>("mode")!;
        if (AccessModePolicy.Parse(mode) is not { } parsed)
        {
            await stderr.WriteLineAsync($"ai-raccoon: invalid access mode '{mode}' (expected ro, rw or full)");
            return 1;
        }

        // The global row IS the wildcard for access (findings): `access set *` is spelled
        // `access default set`.
        if (projectId == "*")
        {
            await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(parsed), cancellationToken);
            await stdout.WriteLineAsync($"access default set to {AccessModePolicy.Serialize(parsed)}");
        }
        else
        {
            await store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), AccessModePolicy.Serialize(parsed),
                cancellationToken);
            await stdout.WriteLineAsync($"access set to {AccessModePolicy.Serialize(parsed)} for project {projectId}");
        }

        return 0;
    }

    private static async Task<int> AccessUnsetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        var projectId = parseResult.GetValue<string>("project-id")!;
        var key = projectId == "*" ? AccessModePolicy.GlobalSettingKey : AccessModePolicy.ProjectSettingKey(projectId);
        await store.DeleteSettingAsync(key, cancellationToken);
        var target = projectId == "*" ? "default" : $"project {projectId}";
        await stdout.WriteLineAsync($"access unset for {target}");
        return 0;
    }

    private static async Task<int> AccessListAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("access.mode.", cancellationToken);
        var global = rows.TryGetValue(AccessModePolicy.GlobalSettingKey, out var raw)
            ? AccessModePolicy.Serialize(AccessModePolicy.Parse(raw) ?? AccessMode.Rw)
            : "rw";
        await stdout.WriteLineAsync($"default: {global}");
        foreach (var (key, value) in rows.Where(kv => kv.Key != AccessModePolicy.GlobalSettingKey).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var projectId = key["access.mode.project:".Length..];
            await stdout.WriteLineAsync($"{projectId}: {value}");
        }

        return 0;
    }

    // ── model ──

    private static async Task<int> ModelSetLocalAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, CancellationToken cancellationToken)
    {
        var path = parseResult.GetResult("path") is not null ? ExpandTilde(parseResult.GetValue<string>("path")) : null;
        // A remote API key is meaningless for the local engine; don't leave it in settings.
        await store.DeleteSettingAsync(EmbeddingSettingsKeys.ApiKey, cancellationToken);
        await store.ConfigureEmbeddingAsync("local", path, null, cancellationToken);
        var modelLabel = path ?? "bundled ONNX model";
        await stdout.WriteLineAsync($"embedding engine set to local ({modelLabel})");
        return 0;
    }

    private static async Task<int> ModelSetOpenAiAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var model = parseResult.GetValue<string>("model");
        var baseUrl = parseResult.GetResult("base-url") is not null ? parseResult.GetValue<string>("base-url") : null;
        var apiKey = parseResult.GetResult("--api-key") is not null ? parseResult.GetValue<string>("--api-key") : null;

        // The key is persisted in settings; write it before the engine so a re-embed
        // triggered by ConfigureEmbeddingAsync can resolve it.
        if (apiKey is not null)
        {
            await store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, apiKey, cancellationToken);
        }
        else if (string.IsNullOrWhiteSpace(await store.GetSettingAsync(EmbeddingSettingsKeys.ApiKey, cancellationToken)))
        {
            await stderr.WriteLineAsync(
                "ai-raccoon: warning — no API key set; run 'ai-raccoon model set openai <model> --api-key <key>' or embeddings will fail");
        }

        await store.ConfigureEmbeddingAsync("openai", model, baseUrl, cancellationToken);
        await stdout.WriteLineAsync($"embedding engine set to openai:{model}");
        return 0;
    }

    private static async Task<int> ModelResetAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        foreach (var key in new[]
                 {
                     EmbeddingSettingsKeys.Provider, EmbeddingSettingsKeys.Model, EmbeddingSettingsKeys.BaseUrl,
                     EmbeddingSettingsKeys.Engine, EmbeddingSettingsKeys.ApiKey
                 })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await stdout.WriteLineAsync("embedding engine reset to default: no engine (FTS5-only search)");
        return 0;
    }

    private static async Task<int> ModelShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("embedding.", cancellationToken);
        var provider = rows.GetValueOrDefault(EmbeddingSettingsKeys.Provider);
        if (string.IsNullOrWhiteSpace(provider))
        {
            await stdout.WriteLineAsync("provider: (none — FTS5-only search)");
            return 0;
        }

        await stdout.WriteLineAsync($"provider: {provider}");
        await stdout.WriteLineAsync($"model: {rows.GetValueOrDefault(EmbeddingSettingsKeys.Model) ?? "(unset)"}");
        await stdout.WriteLineAsync($"baseUrl: {rows.GetValueOrDefault(EmbeddingSettingsKeys.BaseUrl) ?? "(unset)"}");
        await stdout.WriteLineAsync($"engine: {rows.GetValueOrDefault(EmbeddingSettingsKeys.Engine) ?? "(unset)"}");
        var keyState = rows.ContainsKey(EmbeddingSettingsKeys.ApiKey) ? "set" : "unset";
        await stdout.WriteLineAsync($"apiKey: {keyState}");
        return 0;
    }

    // ── retrieval ──

    private static async Task<int> RetrievalAlphaSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("alpha")!;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) ||
            alpha is < 0.0 or > 1.0)
        {
            await stderr.WriteLineAsync($"ai-raccoon: invalid alpha '{raw}' (expected a number in 0..1)");
            return 1;
        }

        await store.SetSettingAsync(StructureFusion.AlphaSettingKey,
            alpha.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await stdout.WriteLineAsync($"retrieval alpha set to {alpha.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static async Task<int> RetrievalAlphaShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var raw = await store.GetSettingAsync(StructureFusion.AlphaSettingKey, cancellationToken);
        var alpha = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : StructureFusion.DefaultAlpha;
        await stdout.WriteLineAsync(alpha.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    // ── sweep (ttl_days removed by ruling — threshold only) ──

    private static async Task<int> SweepThresholdSetAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        var raw = parseResult.GetValue<string>("threshold")!;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var threshold) ||
            threshold is < 0.0 or > 1.0)
        {
            await stderr.WriteLineAsync($"ai-raccoon: invalid threshold '{raw}' (expected a number in 0..1)");
            return 1;
        }

        await store.SetSettingAsync(ForgettingPolicyService.SweepThresholdSettingKey,
            threshold.ToString(CultureInfo.InvariantCulture), cancellationToken);
        await stdout.WriteLineAsync($"sweep threshold set to {threshold.ToString(CultureInfo.InvariantCulture)}");
        return 0;
    }

    private static async Task<int> SweepShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var raw = await store.GetSettingAsync(ForgettingPolicyService.SweepThresholdSettingKey, cancellationToken);
        var threshold = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : ForgettingPolicyService.DefaultSweepThreshold;
        await stdout.WriteLineAsync(threshold.ToString(CultureInfo.InvariantCulture));
        return 0;
    }

    // ── sync ──

    private static async Task<int> SyncAddS3Async(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin, CancellationToken cancellationToken)
    {
        var url = parseResult.GetValue<string>("url")!;
        var bucket = parseResult.GetValue<string>("--bucket")!;
        var region = Optional(parseResult, "--region");
        var objectKey = Optional(parseResult, "--object-key");

        // Secrets are entered interactively (single-channel ruling; never on argv).
        await stderr.WriteAsync("S3 access key (empty aborts): ");
        var accessKey = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(accessKey))
        {
            await stderr.WriteLineAsync("ai-raccoon: access key required — sync not configured");
            return 1;
        }

        await stderr.WriteAsync("S3 secret key (empty aborts): ");
        var secretKey = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(secretKey))
        {
            await stderr.WriteLineAsync("ai-raccoon: secret key required — sync not configured");
            return 1;
        }

        // Writing provider=s3 makes the switch real: without it the factory would read
        // provider=azure and no azure rows → NullCloudStore → silently dead sync.
        await store.SetSettingAsync(SyncSettingsKeys.Provider, "s3", cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.Endpoint, url, cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.Bucket, bucket, cancellationToken);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.Region, region, cancellationToken);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.ObjectKey, objectKey, cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.AccessKey, accessKey, cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.SecretKey, secretKey, cancellationToken);
        foreach (var key in new[] { SyncSettingsKeys.ConnectionString, SyncSettingsKeys.Container })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await stdout.WriteLineAsync($"sync configured: {url} bucket {bucket}");
        return 0;
    }

    private static async Task<int> SyncAddAzureAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin, CancellationToken cancellationToken)
    {
        var container = parseResult.GetValue<string>("container")!;
        var objectKey = Optional(parseResult, "--object-key");

        // Secrets are entered interactively (single-channel ruling; never on argv).
        // Prompt + validate BEFORE any settings write: an abort must leave the current
        // provider untouched (partial writes would spread via the settings merge).
        await stderr.WriteAsync("Azure Blob connection string (empty aborts): ");
        var connectionString = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
        if (string.IsNullOrEmpty(connectionString))
        {
            await stderr.WriteLineAsync("ai-raccoon: connection string required — sync not configured");
            return 1;
        }

        await store.SetSettingAsync(SyncSettingsKeys.Provider, "azure", cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.ConnectionString, connectionString, cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.Container, container, cancellationToken);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.ObjectKey, objectKey, cancellationToken);
        foreach (var key in new[]
                 {
                     SyncSettingsKeys.Endpoint, SyncSettingsKeys.Bucket, SyncSettingsKeys.Region,
                     SyncSettingsKeys.AccessKey, SyncSettingsKeys.SecretKey
                 })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await stdout.WriteLineAsync($"sync configured: azure container {container}");
        return 0;
    }

    private static async Task<int> SyncRemoveAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        // Prefix-delete: "remove deletes ALL sync.* keys" holds by construction and can't
        // drift when rows are added later (single-active-provider ruling R1).
        var rows = await store.GetSettingsByPrefixAsync("sync.", cancellationToken);
        foreach (var key in rows.Keys)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await stdout.WriteLineAsync("sync removed (sync off)");
        return 0;
    }

    private static async Task<int> SyncShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("sync.", cancellationToken);
        if (!rows.TryGetValue(SyncSettingsKeys.Endpoint, out var endpoint))
        {
            await stdout.WriteLineAsync("sync not configured");
            return 0;
        }

        await stdout.WriteLineAsync($"endpoint: {endpoint}");
        await stdout.WriteLineAsync($"bucket: {rows.GetValueOrDefault(SyncSettingsKeys.Bucket) ?? "(unset)"}");
        await stdout.WriteLineAsync($"region: {rows.GetValueOrDefault(SyncSettingsKeys.Region) ?? "(unset)"}");
        await stdout.WriteLineAsync($"objectKey: {rows.GetValueOrDefault(SyncSettingsKeys.ObjectKey) ?? "(unset)"}");
        var accessState = rows.ContainsKey(SyncSettingsKeys.AccessKey) ? "set" : "unset";
        var secretState = rows.ContainsKey(SyncSettingsKeys.SecretKey) ? "set" : "unset";
        await stdout.WriteLineAsync($"accessKey: {accessState}");
        await stdout.WriteLineAsync($"secretKey: {secretState}");
        return 0;
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
                "ai-raccoon: warning — no watch scopes configured; add at least one scope with 'ai-raccoon watch scope add * <path>'");
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
            var enabled = rows.GetValueOrDefault(WatchConfigKeys.EnabledProject(target))
                          ?? rows.GetValueOrDefault(WatchConfigKeys.EnabledGlobal)
                          ?? "false";
            var concurrencyRaw = rows.GetValueOrDefault(WatchConfigKeys.ConcurrencyProject(target))
                                 ?? rows.GetValueOrDefault(WatchConfigKeys.ConcurrencyGlobal);
            var concurrency = int.TryParse(concurrencyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : 4;
            var scope = WatchScopeList.Parse(rows.GetValueOrDefault(WatchConfigKeys.ScopeProject(target))
                                             ?? rows.GetValueOrDefault(WatchConfigKeys.ScopeGlobal));
            await stdout.WriteLineAsync($"{target}: enabled={enabled} concurrency={concurrency} scope={WatchScopeList.ToJson(scope)}");
        }

        return 0;
    }

    private static string? Optional(ParseResult parseResult, string name) => parseResult.GetResult(name) is not null ? parseResult.GetValue<string>(name) : null;

    private static async Task UpsertOrDeleteAsync(IMemoryStore store, string key, string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }
        else
        {
            await store.SetSettingAsync(key, value, cancellationToken);
        }
    }

    private static string? ExpandTilde(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path == "~" ? home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, path[2..]) : path;
    }
}
