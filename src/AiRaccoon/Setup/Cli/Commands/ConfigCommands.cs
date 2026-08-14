using AiRaccoon.Core.Memory;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot config commands (the single runtime config channel): each verb opens the bank
///     through the given store, applies settings-table writes, and returns the exit code.
///     User-run commands get no access-tier checks; errors go to stderr, results to stdout.
/// </summary>
internal sealed class ConfigCommands(
    IMemoryStore store,
    SettingsCommands settings,
    SyncCommands sync,
    WatchCommands watch,
    EncryptionCommands encryptionCommands,
    ExtractCommands extract,
    MaintenanceCommands maintenance,
    ServeCommands serve)
{
    public async Task<int> RunAsync(CliInput cliInput,
        StandardStreams streams,
        CancellationToken ctx)
    {
        var commandPath = cliInput.CommandPath;
        var parsedCliArgs = cliInput.ParsedCliArgs;

        // CliRendering already printed the parse error(s) before this ran (AppRunner.GetCliInput);
        // dispatching anyway would throw reading an argument System.CommandLine never bound,
        // landing in the catch below and reformatting the same message a second time.
        if (cliInput.Errors.Count > 0)
        {
            return ExitCode.InvalidArgument;
        }

        try
        {
            return commandPath switch
            {
                ["access", "default", "set"] => await settings.AccessDefaultSetAsync(parsedCliArgs, store, streams, ctx),
                ["access", "default", "show"] =>
                    await settings.AccessDefaultShowAsync(store, streams, ctx),
                ["access", "set"] => await settings.AccessSetAsync(parsedCliArgs, store, streams,
                    ctx),
                ["access", "unset"] => await settings.AccessUnsetAsync(parsedCliArgs, store, streams, ctx),
                ["access", "list"] => await settings.AccessListAsync(store, streams, ctx),
                ["model", "set", "local"] => await settings.ModelSetLocalAsync(parsedCliArgs, store, streams,
                    ctx),
                ["model", "set", "openai"] => await settings.ModelSetOpenAiAsync(parsedCliArgs, store, streams, ctx),
                ["model", "reset"] => await settings.ModelResetAsync(store, streams, ctx),
                ["model", "show"] => await settings.ModelShowAsync(store, streams, ctx),
                ["retrieval", "alpha", "set"] => await settings.RetrievalAlphaSetAsync(parsedCliArgs, store, streams, ctx),
                ["retrieval", "alpha", "show"] => await settings.RetrievalAlphaShowAsync(store, streams,
                    ctx),
                ["sweep", "enable"] => await settings.SweepEnabledSetAsync(true, store, streams, ctx),
                ["sweep", "disable"] => await settings.SweepEnabledSetAsync(false, store, streams, ctx),
                ["sweep", "interval-hours"] => await settings.SweepIntervalHoursSetAsync(parsedCliArgs, store, streams, ctx),
                ["sweep", "threshold", "set"] => await settings.SweepThresholdSetAsync(parsedCliArgs, store, streams, ctx),
                ["sweep", "show"] => await settings.SweepShowAsync(store, streams, ctx),
                ["noise", "enable"] => await settings.NoiseEnabledSetAsync(true, store, streams, ctx),
                ["noise", "disable"] => await settings.NoiseEnabledSetAsync(false, store, streams, ctx),
                ["noise", "show"] => await settings.NoiseShowAsync(store, streams, ctx),
                ["queryguard", "enable"] => await settings.QueryGuardEnabledSetAsync(true, store, streams, ctx),
                ["queryguard", "disable"] => await settings.QueryGuardEnabledSetAsync(false, store, streams, ctx),
                ["queryguard", "shadow", "enable"] => await settings.QueryGuardShadowSetAsync(true, store, streams, ctx),
                ["queryguard", "shadow", "disable"] => await settings.QueryGuardShadowSetAsync(false, store, streams, ctx),
                ["queryguard", "show"] => await settings.QueryGuardShowAsync(store, streams, ctx),
                ["sync", "add", "s3"] => await sync.AddS3Async(parsedCliArgs, store, streams, ctx),
                ["sync", "add", "azure"] => await sync.AddAzureAsync(parsedCliArgs, store, streams,
                    ctx),
                ["sync", "remove"] => await sync.RemoveAsync(store, streams, ctx),
                ["sync", "show"] => await sync.ShowAsync(store, streams, ctx),
                ["watch", "enable"] or ["watch", "disable"] => await watch.SetEnabledAsync(parsedCliArgs, store, streams,
                    ctx),
                ["ingest", "scope", "add"] or ["watch", "scope", "add"] => await watch.ScopeAddAsync(parsedCliArgs, store,
                    streams, ctx),
                ["ingest", "scope", "remove"] or ["watch", "scope", "remove"] => await watch.ScopeRemoveAsync(parsedCliArgs,
                    store, streams, ctx),
                ["ingest", "scope", "list"] or ["watch", "scope", "list"] => await watch.ScopeListAsync(parsedCliArgs, store,
                    streams, ctx),
                ["watch", "concurrency"] => await watch.ConcurrencyAsync(parsedCliArgs, store, streams,
                    ctx),
                ["watch", "list"] => await watch.ListAsync(store, streams, ctx),
                ["watch", "registered"] => await watch.RegisteredAsync(parsedCliArgs, streams, ctx),
                ["watch", "remove"] => await watch.RemoveAsync(parsedCliArgs, store, streams, ctx),
                ["encryption", "bitwarden"] => await encryptionCommands.BitwardenAsync(parsedCliArgs, store,
                    streams, ctx),
                ["extract", "enable"] => await extract.SetEnabledAsync(parsedCliArgs, store, streams, ctx),
                ["extract", "mode"] => await extract.SetModeAsync(parsedCliArgs, store, streams, ctx),
                ["extract", "interval"] => await extract.SetIntervalAsync(parsedCliArgs, store, streams,
                    ctx),
                ["extract", "capacity"] => await extract.SetCapacityAsync(parsedCliArgs, store, streams,
                    ctx),
                ["extract", "list"] => await extract.ListAsync(store, streams, ctx),
                ["extract", "exclude", "add"] => await extract.ExcludeAddAsync(parsedCliArgs, store, streams,
                    ctx),
                ["extract", "exclude", "remove"] => await extract.ExcludeRemoveAsync(parsedCliArgs, store, streams,
                    ctx),
                ["extract", "exclude", "list"] => await extract.ExcludeListAsync(store, streams, ctx),
                ["extract", "prune"] => await extract.PruneAsync(parsedCliArgs, streams, ctx),
                ["encryption", "show"] => await encryptionCommands.ShowAsync(store, streams,
                    ctx),
                ["encryption", "unset"] => await encryptionCommands.UnsetAsync(store, streams,
                    ctx),
                ["encryption", "migrate"] => await encryptionCommands.MigrateAsync(streams,
                    ctx),
                ["maintenance", "interval"] => await maintenance.SetCheckpointIntervalAsync(parsedCliArgs, store,
                    streams, ctx),
                ["maintenance", "vacuum-interval"] => await maintenance.SetVacuumIntervalAsync(parsedCliArgs, store,
                    streams, ctx),
                ["maintenance", "list"] => await maintenance.ListAsync(store, streams, ctx),
                ["serve", "observability"] => await serve.Observability(cliInput, streams, ctx),
                ["serve"] => await serve.StartNode(cliInput, streams, ctx),
                _ => throw new InvalidOperationException($"unhandled command: {string.Join(' ', commandPath)}")
            };
        }
        catch (Exception ex)
        {
            await streams.WriteErrorLineAsync(CliFailureFormatting.Format(ex, cliInput.ServerConfig.Options.DataRoot));
            return ExitCode.InvalidArgument;
        }
    }
}
