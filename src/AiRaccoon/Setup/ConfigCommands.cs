using System.CommandLine;
using System.CommandLine.Parsing;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;

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
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
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
