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
        IWatchCommands? watch = null, IEncryptionCommands? encryptionCommands = null,
        IExtractCommands? extract = null,
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
                ["watch", "enable"] or ["watch", "disable"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).SetEnabledAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["watch", "scope", "add"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).ScopeAddAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "scope", "remove"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).ScopeRemoveAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "scope", "list"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).ScopeListAsync(parseResult, store, stdout, cancellationToken),
                ["watch", "concurrency"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).ConcurrencyAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["watch", "list"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).ListAsync(store, stdout, cancellationToken),
                ["watch", "registered"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).RegisteredAsync(parseResult, stdout, cancellationToken),
                ["watch", "remove"] => await (watch ?? ThrowHelper.ThrowArgumentNullException<IWatchCommands>(nameof(watch))).RemoveAsync(parseResult, store, stdout, cancellationToken),
                ["encryption", "bitwarden"] => await (encryptionCommands ?? ThrowHelper.ThrowArgumentNullException<IEncryptionCommands>(nameof(encryptionCommands))).BitwardenAsync(parseResult, store, stdout, stderr, stdin, cancellationToken),
                ["extract", "enable"] => await (extract ?? ThrowHelper.ThrowArgumentNullException<IExtractCommands>(nameof(extract))).SetEnabledAsync(parseResult, store, stdout, cancellationToken),
                ["extract", "mode"] => await (extract ?? ThrowHelper.ThrowArgumentNullException<IExtractCommands>(nameof(extract))).SetModeAsync(parseResult, store, stdout, stderr, cancellationToken),
                ["extract", "list"] => await (extract ?? ThrowHelper.ThrowArgumentNullException<IExtractCommands>(nameof(extract))).ListAsync(store, stdout, cancellationToken),
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

}
