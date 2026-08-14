using System.CommandLine;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot sync verb handlers: interactive secrets, delete-before-write provider swap.</summary>
public sealed class SyncCommands
{
    public async Task<int> AddS3Async(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams,
        CancellationToken ctx)
    {
        var url = parseResult.GetValue<string>("url")!;
        var bucket = parseResult.GetValue<string>("--bucket")!;
        var region = Optional(parseResult, "--region");
        var objectKey = Optional(parseResult, "--object-key");
        var useChain = parseResult.GetValue<bool>("--cli");

        string? accessKey = null;
        string? secretKey = null;
        if (!useChain)
        {
            // Secrets are entered interactively (single-channel ruling; never on argv).
            await streams.WriteErrorAsync("S3 access key (empty aborts): ");
            accessKey = (await streams.ReadLineAsync(ctx))?.Trim();
            if (string.IsNullOrEmpty(accessKey))
            {
                await streams.WriteErrorLineAsync("ai-raccoon: access key required — sync not configured");
                return ExitCode.InvalidArgument;
            }

            await streams.WriteErrorAsync("S3 secret key (empty aborts): ");
            secretKey = (await streams.ReadLineAsync(ctx))?.Trim();
            if (string.IsNullOrEmpty(secretKey))
            {
                await streams.WriteErrorLineAsync("ai-raccoon: secret key required — sync not configured");
                return ExitCode.InvalidArgument;
            }
        }

        // One active provider (docs/plans/azure-blob-sync-plan.md R1): drop the other backend's
        // rows first so a crash between delete and write can't leave stale secrets behind.
        foreach (var key in new[]
                 {
                     SyncSettingsKeys.ConnectionString, SyncSettingsKeys.Container, SyncSettingsKeys.AzureAccount
                 })
        {
            await store.DeleteSettingAsync(key, ctx);
        }

        if (useChain)
        {
            // Chain mode: stale persisted keys would win the tie-break — clear them.
            await store.DeleteSettingAsync(SyncSettingsKeys.AccessKey, ctx);
            await store.DeleteSettingAsync(SyncSettingsKeys.SecretKey, ctx);
        }
        else
        {
            await store.DeleteSettingAsync(SyncSettingsKeys.S3Chain, ctx);
        }

        // Writing provider=s3 makes the switch real: without it the factory would read
        // provider=azure and no azure rows → NullCloudStore → silently dead sync.
        await store.SetSettingAsync(SyncSettingsKeys.Provider, "s3", ctx);
        await store.SetSettingAsync(SyncSettingsKeys.Endpoint, url, ctx);
        await store.SetSettingAsync(SyncSettingsKeys.Bucket, bucket, ctx);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.Region, region, ctx);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.ObjectKey, objectKey, ctx);
        if (useChain)
        {
            await store.SetSettingAsync(SyncSettingsKeys.S3Chain, "true", ctx);
        }
        else
        {
            await store.SetSettingAsync(SyncSettingsKeys.AccessKey,
                accessKey ?? ThrowHelper.ThrowArgumentNullException<string>(nameof(accessKey)), ctx);
            await store.SetSettingAsync(SyncSettingsKeys.SecretKey,
                secretKey ?? ThrowHelper.ThrowArgumentNullException<string>(nameof(secretKey)), ctx);
        }

        await streams.WriteOutputLineAsync(useChain
            ? $"sync configured: {url} bucket {bucket} (AWS credential chain)"
            : $"sync configured: {url} bucket {bucket}");
        return 0;
    }

    public async Task<int> AddAzureAsync(ParseResult parseResult, IMemoryStore store,
        StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var container = parseResult.GetValue<string>("container")!;
        var objectKey = Optional(parseResult, "--object-key");
        var useCli = parseResult.GetValue<bool>("--cli");

        string? connectionString = null;
        string? account = null;
        if (useCli)
        {
            account = Optional(parseResult, "--account");
            if (account is null)
            {
                await streams.WriteErrorLineAsync("ai-raccoon: --account is required with --cli");
                return ExitCode.InvalidArgument;
            }
        }
        else
        {
            // Secrets are entered interactively (single-channel ruling; never on argv).
            // Prompt + validate BEFORE any settings write: an abort must leave the current
            // provider untouched (partial writes would spread via the settings merge).
            await streams.WriteErrorAsync("Azure Blob connection string (empty aborts): ");
            connectionString = (await streams.ReadLineAsync(cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(connectionString))
            {
                await streams.WriteErrorLineAsync("ai-raccoon: connection string required — sync not configured");
                return ExitCode.InvalidArgument;
            }
        }

        // One active provider (docs/plans/azure-blob-sync-plan.md R1): drop the other backend's
        // rows first so a crash between delete and write can't leave stale secrets behind.
        foreach (var key in new[]
                 {
                     SyncSettingsKeys.Endpoint, SyncSettingsKeys.Bucket, SyncSettingsKeys.Region,
                     SyncSettingsKeys.AccessKey, SyncSettingsKeys.SecretKey, SyncSettingsKeys.S3Chain
                 })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        if (useCli)
        {
            // --cli mode: a stale connection string would win the tie-break — clear it.
            await store.DeleteSettingAsync(SyncSettingsKeys.ConnectionString, cancellationToken);
        }
        else
        {
            await store.DeleteSettingAsync(SyncSettingsKeys.AzureAccount, cancellationToken);
        }

        await store.SetSettingAsync(SyncSettingsKeys.Provider, "azure", cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.Container, container, cancellationToken);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.ObjectKey, objectKey, cancellationToken);
        if (useCli)
        {
            await store.SetSettingAsync(SyncSettingsKeys.AzureAccount,
                account ?? ThrowHelper.ThrowArgumentNullException<string>(nameof(account)), cancellationToken);
        }
        else
        {
            await store.SetSettingAsync(SyncSettingsKeys.ConnectionString,
                connectionString ?? ThrowHelper.ThrowArgumentNullException<string>(nameof(connectionString)), cancellationToken);
        }

        await streams.WriteOutputLineAsync(useCli
            ? $"sync configured: azure container {container} (az CLI)"
            : $"sync configured: azure container {container}");
        return 0;
    }

    public async Task<int> RemoveAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        // Prefix-delete: "remove deletes ALL sync.* keys" holds by construction and can't drift
        // when rows are added later (docs/plans/azure-blob-sync-plan.md R1).
        var rows = await store.GetSettingsByPrefixAsync("sync.", cancellationToken);
        foreach (var key in rows.Keys)
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        await streams.WriteOutputLineAsync("sync removed (sync off)");
        return 0;
    }

    public async Task<int> ShowAsync(IMemoryStore store, StandardStreams streams,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("sync.", cancellationToken);
        if (rows.Count == 0)
        {
            await streams.WriteOutputLineAsync("sync not configured");
            return 0;
        }

        // This is the command an operator runs to FIND a typo, so it reports an unrecognised row
        // rather than throwing the way the factory does (A-F8).
        var rawProvider = rows.GetValueOrDefault(SyncSettingsKeys.Provider) ?? "s3";
        var recognised = SyncProviderParser.TryParse(rawProvider, out var shownProvider);
        await streams.WriteOutputLineAsync(recognised
            ? $"provider: {rawProvider}"
            : $"provider: {rawProvider}  (not a supported backend — sync will refuse; expected s3 or azure)");
        if (recognised && shownProvider == SyncProvider.Azure)
        {
            await streams.WriteOutputLineAsync($"container: {rows.GetValueOrDefault(SyncSettingsKeys.Container) ?? "(unset)"}");
            await streams.WriteOutputLineAsync($"objectKey: {rows.GetValueOrDefault(SyncSettingsKeys.ObjectKey) ?? "(unset)"}");
            var connectionState = rows.ContainsKey(SyncSettingsKeys.ConnectionString) ? "set" : "unset";
            await streams.WriteOutputLineAsync($"connectionString: {connectionState}");
            var accountState = rows.ContainsKey(SyncSettingsKeys.AzureAccount) ? "set" : "unset";
            await streams.WriteOutputLineAsync($"account: {accountState}");
            return 0;
        }

        await streams.WriteOutputLineAsync($"endpoint: {rows.GetValueOrDefault(SyncSettingsKeys.Endpoint) ?? "(unset)"}");
        await streams.WriteOutputLineAsync($"bucket: {rows.GetValueOrDefault(SyncSettingsKeys.Bucket) ?? "(unset)"}");
        await streams.WriteOutputLineAsync($"region: {rows.GetValueOrDefault(SyncSettingsKeys.Region) ?? "(unset)"}");
        await streams.WriteOutputLineAsync($"objectKey: {rows.GetValueOrDefault(SyncSettingsKeys.ObjectKey) ?? "(unset)"}");
        var accessState = rows.ContainsKey(SyncSettingsKeys.AccessKey) ? "set" : "unset";
        var secretState = rows.ContainsKey(SyncSettingsKeys.SecretKey) ? "set" : "unset";
        await streams.WriteOutputLineAsync($"accessKey: {accessState}");
        await streams.WriteOutputLineAsync($"secretKey: {secretState}");
        var chain = bool.TryParse(rows.GetValueOrDefault(SyncSettingsKeys.S3Chain), out var parsedChain) && parsedChain;
        await streams.WriteOutputLineAsync($"chain: {(chain ? "true" : "false")}");
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
}
