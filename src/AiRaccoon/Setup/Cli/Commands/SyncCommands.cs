using System.CommandLine;
using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>One-shot sync verb handlers: interactive secrets, delete-before-write provider swap.</summary>
public sealed class SyncCommands : ISyncCommands
{
    public async Task<int> AddS3Async(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
        CancellationToken cancellationToken)
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
            await stderr.WriteAsync("S3 access key (empty aborts): ");
            accessKey = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(accessKey))
            {
                await stderr.WriteLineAsync("ai-raccoon: access key required — sync not configured");
                return 1;
            }

            await stderr.WriteAsync("S3 secret key (empty aborts): ");
            secretKey = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(secretKey))
            {
                await stderr.WriteLineAsync("ai-raccoon: secret key required — sync not configured");
                return 1;
            }
        }

        // R1: one active provider — drop the other backend's rows and the stale mode row
        // first so a crash between delete and write can't leave stale secrets behind.
        foreach (var key in new[]
                 {
                     SyncSettingsKeys.ConnectionString, SyncSettingsKeys.Container, SyncSettingsKeys.AzureAccount
                 })
        {
            await store.DeleteSettingAsync(key, cancellationToken);
        }

        if (useChain)
        {
            // Chain mode: stale persisted keys would win the tie-break — clear them.
            await store.DeleteSettingAsync(SyncSettingsKeys.AccessKey, cancellationToken);
            await store.DeleteSettingAsync(SyncSettingsKeys.SecretKey, cancellationToken);
        }
        else
        {
            await store.DeleteSettingAsync(SyncSettingsKeys.S3Chain, cancellationToken);
        }

        // Writing provider=s3 makes the switch real: without it the factory would read
        // provider=azure and no azure rows → NullCloudStore → silently dead sync.
        await store.SetSettingAsync(SyncSettingsKeys.Provider, "s3", cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.Endpoint, url, cancellationToken);
        await store.SetSettingAsync(SyncSettingsKeys.Bucket, bucket, cancellationToken);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.Region, region, cancellationToken);
        await UpsertOrDeleteAsync(store, SyncSettingsKeys.ObjectKey, objectKey, cancellationToken);
        if (useChain)
        {
            await store.SetSettingAsync(SyncSettingsKeys.S3Chain, "true", cancellationToken);
        }
        else
        {
            await store.SetSettingAsync(SyncSettingsKeys.AccessKey,
                accessKey ?? ThrowHelper.ThrowArgumentNullException<string>(nameof(accessKey)), cancellationToken);
            await store.SetSettingAsync(SyncSettingsKeys.SecretKey,
                secretKey ?? ThrowHelper.ThrowArgumentNullException<string>(nameof(secretKey)), cancellationToken);
        }

        await stdout.WriteLineAsync(useChain
            ? $"sync configured: {url} bucket {bucket} (AWS credential chain)"
            : $"sync configured: {url} bucket {bucket}");
        return 0;
    }

    public async Task<int> AddAzureAsync(ParseResult parseResult, IMemoryStore store,
        TextWriter stdout, TextWriter stderr, TextReader stdin,
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
                await stderr.WriteLineAsync("ai-raccoon: --account is required with --cli");
                return 1;
            }
        }
        else
        {
            // Secrets are entered interactively (single-channel ruling; never on argv).
            // Prompt + validate BEFORE any settings write: an abort must leave the current
            // provider untouched (partial writes would spread via the settings merge).
            await stderr.WriteAsync("Azure Blob connection string (empty aborts): ");
            connectionString = (await stdin.ReadLineAsync(cancellationToken))?.Trim();
            if (string.IsNullOrEmpty(connectionString))
            {
                await stderr.WriteLineAsync("ai-raccoon: connection string required — sync not configured");
                return 1;
            }
        }

        // R1: one active provider — drop the other backend's rows and the stale mode row
        // first so a crash between delete and write can't leave stale secrets behind.
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

        await stdout.WriteLineAsync(useCli
            ? $"sync configured: azure container {container} (az CLI)"
            : $"sync configured: azure container {container}");
        return 0;
    }

    public async Task<int> RemoveAsync(IMemoryStore store, TextWriter stdout,
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

    public async Task<int> ShowAsync(IMemoryStore store, TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var rows = await store.GetSettingsByPrefixAsync("sync.", cancellationToken);
        if (rows.Count == 0)
        {
            await stdout.WriteLineAsync("sync not configured");
            return 0;
        }

        // R2 — unknown values route as s3; the raw row is printed so a typo is diagnosable.
        var rawProvider = rows.GetValueOrDefault(SyncSettingsKeys.Provider) ?? "s3";
        await stdout.WriteLineAsync($"provider: {rawProvider}");
        if (SyncProviderParser.Parse(rawProvider) == SyncProvider.Azure)
        {
            await stdout.WriteLineAsync($"container: {rows.GetValueOrDefault(SyncSettingsKeys.Container) ?? "(unset)"}");
            await stdout.WriteLineAsync($"objectKey: {rows.GetValueOrDefault(SyncSettingsKeys.ObjectKey) ?? "(unset)"}");
            var connectionState = rows.ContainsKey(SyncSettingsKeys.ConnectionString) ? "set" : "unset";
            await stdout.WriteLineAsync($"connectionString: {connectionState}");
            var accountState = rows.ContainsKey(SyncSettingsKeys.AzureAccount) ? "set" : "unset";
            await stdout.WriteLineAsync($"account: {accountState}");
            return 0;
        }

        await stdout.WriteLineAsync($"endpoint: {rows.GetValueOrDefault(SyncSettingsKeys.Endpoint) ?? "(unset)"}");
        await stdout.WriteLineAsync($"bucket: {rows.GetValueOrDefault(SyncSettingsKeys.Bucket) ?? "(unset)"}");
        await stdout.WriteLineAsync($"region: {rows.GetValueOrDefault(SyncSettingsKeys.Region) ?? "(unset)"}");
        await stdout.WriteLineAsync($"objectKey: {rows.GetValueOrDefault(SyncSettingsKeys.ObjectKey) ?? "(unset)"}");
        var accessState = rows.ContainsKey(SyncSettingsKeys.AccessKey) ? "set" : "unset";
        var secretState = rows.ContainsKey(SyncSettingsKeys.SecretKey) ? "set" : "unset";
        await stdout.WriteLineAsync($"accessKey: {accessState}");
        await stdout.WriteLineAsync($"secretKey: {secretState}");
        var chain = bool.TryParse(rows.GetValueOrDefault(SyncSettingsKeys.S3Chain), out var parsedChain) && parsedChain;
        await stdout.WriteLineAsync($"chain: {(chain ? "true" : "false")}");
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
