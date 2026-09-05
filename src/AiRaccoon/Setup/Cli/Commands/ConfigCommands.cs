using AiRaccoon.Core.Memory;
using AiRaccoon.Settings;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>
///     One-shot config commands (the single runtime config channel): each verb opens the bank
///     through the given store, applies settings-table writes, and returns the exit code.
///     User-run commands get no access-tier checks; errors go to stderr, results to stdout.
/// </summary>
internal sealed class ConfigCommands(
    IMemoryStore store,
    IModelMigrationStore modelMigrations,
    ICodeEngineStore codeEngine,
    SettingsCommands settings,
    SyncCommands sync,
    WatchCommands watch,
    EncryptionCommands encryptionCommands,
    ExtractCommands extract,
    MaintenanceCommands maintenance,
    PerformanceCommands performance,
    ServeCommands serve,
    NoiseEntriesCommands noiseEntries,
    ChunkIndexRepairCommands chunkIndexRepair,
    ReingestRepairCommands reingestRepair,
    ProjectIdsRepairCommands projectIdsRepair,
    DoctorCommands doctor,
    ModelDownloadCommands modelDownload)
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
                ["settings", "access", "default", "set"] => await settings.AccessDefaultSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "access", "default", "show"] => await settings.AccessDefaultShowAsync(store, streams, ctx),
                ["settings", "access", "set"] => await settings.AccessSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "access", "unset"] => await settings.AccessUnsetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "access", "list"] => await settings.AccessListAsync(store, streams, ctx),
                ["model", "embedding", "set", "local"] => await settings.ModelSetLocalAsync(parsedCliArgs, store, modelMigrations, streams, ctx),
                ["model", "embedding", "set", "openai"] => await settings.ModelSetOpenAiAsync(parsedCliArgs, store, modelMigrations, streams, ctx),
                ["model", "code", "set", "local"] => await settings.ModelSetCodeLocalAsync(parsedCliArgs, codeEngine, streams, ctx),
                ["model", "code", "set", "default"] => await settings.ModelSetCodeDefaultAsync(modelDownload, codeEngine, cliInput.Options.DataRoot, streams, ctx),
                ["model", "download"] => await modelDownload.RunAsync(parsedCliArgs, cliInput.Options.DataRoot, streams, ctx),
                ["settings", "model", "embedding", "reset"] => await settings.ModelResetAsync(store, streams, ctx),
                ["settings", "model", "embedding", "show"] => await settings.ModelEmbeddingShowAsync(store, streams, ctx),
                ["settings", "model", "code", "reset"] => await settings.ModelCodeResetAsync(store, streams, ctx),
                ["settings", "model", "code", "show"] => await settings.ModelCodeShowAsync(store, streams, ctx),
                ["settings", "model", "reset"] => await settings.ModelResetAsync(store, streams, ctx),
                ["settings", "model", "show"] => await settings.ModelShowAsync(store, streams, ctx),
                ["settings", "model", "threads"] => await settings.ModelThreadsSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "alpha", "set"] => await settings.RetrievalAlphaSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "alpha", "show"] => await settings.RetrievalAlphaShowAsync(store, streams, ctx),
                ["settings", "retrieval", "fusion", "enable"] => await settings.RetrievalFusionSetAsync(true, store, streams, ctx),
                ["settings", "retrieval", "fusion", "disable"] => await settings.RetrievalFusionSetAsync(false, store, streams, ctx),
                ["settings", "retrieval", "fusion", "show"] => await settings.RetrievalFusionShowAsync(store, streams, ctx),
                ["settings", "retrieval", "rrfk", "set"] => await settings.RetrievalRrfKSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "rrfk", "show"] => await settings.RetrievalRrfKShowAsync(store, streams, ctx),
                ["settings", "retrieval", "fts-weight", "set"] => await settings.RetrievalFtsWeightSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "fts-weight", "show"] => await settings.RetrievalFtsWeightShowAsync(store, streams, ctx),
                ["settings", "retrieval", "vector-weight", "set"] => await settings.RetrievalVectorWeightSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "vector-weight", "show"] => await settings.RetrievalVectorWeightShowAsync(store, streams, ctx),
                ["settings", "retrieval", "source-lambda", "set"] => await settings.RetrievalSourceLambdaSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "source-lambda", "show"] => await settings.RetrievalSourceLambdaShowAsync(store, streams, ctx),
                ["settings", "retrieval", "consolidation", "set"] => await settings.RetrievalConsolidationSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "consolidation", "show"] => await settings.RetrievalConsolidationShowAsync(store, streams, ctx),
                ["settings", "retrieval", "doc-formula", "set"] => await settings.RetrievalDocFormulaSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "doc-formula", "show"] => await settings.RetrievalDocFormulaShowAsync(store, streams, ctx),
                ["settings", "retrieval", "window", "set"] => await settings.RetrievalWindowSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "retrieval", "window", "show"] => await settings.RetrievalWindowShowAsync(store, streams, ctx),
                ["settings", "retrieval", "show-all"] => await settings.RetrievalShowAllAsync(store, streams, ctx),
                ["settings", "sweep", "enable"] => await settings.SweepEnabledSetAsync(true, store, streams, ctx),
                ["settings", "sweep", "disable"] => await settings.SweepEnabledSetAsync(false, store, streams, ctx),
                ["settings", "sweep", "interval-hours"] => await settings.SweepIntervalHoursSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "sweep", "threshold", "set"] => await settings.SweepThresholdSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "sweep", "show"] => await settings.SweepShowAsync(store, streams, ctx),
                ["settings", "noise", "enable"] => await settings.NoiseEnabledSetAsync(true, store, streams, ctx),
                ["settings", "noise", "disable"] => await settings.NoiseEnabledSetAsync(false, store, streams, ctx),
                ["settings", "noise", "show"] => await settings.NoiseShowAsync(store, streams, ctx),
                ["noise", "entries"] => await noiseEntries.SummarizeAsync(streams, ctx),
                ["settings", "queryguard", "enable"] => await settings.QueryGuardEnabledSetAsync(true, store, streams, ctx),
                ["settings", "queryguard", "disable"] => await settings.QueryGuardEnabledSetAsync(false, store, streams, ctx),
                ["settings", "queryguard", "shadow", "enable"] => await settings.QueryGuardShadowSetAsync(true, store, streams, ctx),
                ["settings", "queryguard", "shadow", "disable"] => await settings.QueryGuardShadowSetAsync(false, store, streams, ctx),
                ["settings", "queryguard", "structural", "enable"] => await settings.QueryGuardStructuralSetAsync(true, store, streams, ctx),
                ["settings", "queryguard", "structural", "disable"] => await settings.QueryGuardStructuralSetAsync(false, store, streams, ctx),
                ["settings", "queryguard", "structural", "threshold", "set"] =>
                    await settings.QueryGuardStructuralThresholdSetAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "queryguard", "show"] => await settings.QueryGuardShowAsync(store, streams, ctx),
                ["settings", "sync", "add", "s3"] => await sync.AddS3Async(parsedCliArgs, store, streams, ctx),
                ["settings", "sync", "add", "azure"] => await sync.AddAzureAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "sync", "remove"] => await sync.RemoveAsync(store, streams, ctx),
                ["settings", "sync", "show"] => await sync.ShowAsync(store, streams, ctx),
                ["settings", "watch", "enable"] or ["settings", "watch", "disable"] => await watch.SetEnabledAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "ingest", "scope", "add"] => await watch.ScopeAddAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "ingest", "scope", "remove"] => await watch.ScopeRemoveAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "ingest", "scope", "list"] => await watch.ScopeListAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "watch", "concurrency"] => await watch.ConcurrencyAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "watch", "list"] => await watch.ListAsync(store, streams, ctx),
                ["settings", "watch", "remove"] => await watch.RemoveAsync(parsedCliArgs, store, streams, ctx),
                ["watch", "registered"] => await watch.RegisteredAsync(parsedCliArgs, streams, ctx),
                ["encryption", "bitwarden"] => await encryptionCommands.BitwardenAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "enable"] => await extract.SetEnabledAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "mode"] => await extract.SetModeAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "interval"] => await extract.SetIntervalAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "capacity"] => await extract.SetCapacityAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "auto-promote-threshold"] => await extract.SetAutoPromoteThresholdAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "list"] => await extract.ListAsync(store, streams, ctx),
                ["settings", "extract", "exclude", "add"] => await extract.ExcludeAddAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "exclude", "remove"] => await extract.ExcludeRemoveAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "extract", "exclude", "list"] => await extract.ExcludeListAsync(store, streams, ctx),
                ["extract", "prune"] => await extract.PruneAsync(parsedCliArgs, streams, ctx),
                ["repair", "chunk-index"] => await chunkIndexRepair.RunAsync(parsedCliArgs, streams, ctx),
                ["repair", "reingest"] => await reingestRepair.RunAsync(parsedCliArgs, streams, ctx),
                ["repair", "project-ids"] => await projectIdsRepair.RunAsync(parsedCliArgs, cliInput.Options.DataRoot, streams, ctx),
                ["encryption", "show"] => await encryptionCommands.ShowAsync(store, streams, ctx),
                ["encryption", "unset"] => await encryptionCommands.UnsetAsync(store, streams, ctx),
                ["encryption", "migrate"] => await encryptionCommands.MigrateAsync(streams, ctx),
                ["settings", "maintenance", "interval"] => await maintenance.SetCheckpointIntervalAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "maintenance", "vacuum-interval"] => await maintenance.SetVacuumIntervalAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "maintenance", "embed-rows-per-run"] => await maintenance.SetEmbedRowsPerRunAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "maintenance", "list"] => await maintenance.ListAsync(store, streams, ctx),
                ["settings", "performance", "buffer-capacity"] => await performance.SetBufferCapacityAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "performance", "flush-interval"] => await performance.SetFlushIntervalAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "performance", "retention"] => await performance.SetRetentionDaysAsync(parsedCliArgs, store, streams, ctx),
                ["settings", "performance", "list"] => await performance.ListAsync(store, streams, ctx),
                ["serve", "observability"] => await serve.Observability(cliInput, streams, ctx),
                ["serve"] => await serve.StartNode(cliInput, streams, ctx),
                ["doctor"] => await doctor.RunAsync(streams, ctx),
                _ => throw new InvalidOperationException($"unhandled command: {string.Join(' ', commandPath)}")
            };
        }
        catch (SettingsServerRefusedException ex)
        {
            // The exception message already carries the "ai-raccoon: " prefix (ServerSettingsStore),
            // so it goes to stderr unreformatted rather than through CliFailureFormatting, which
            // would double it.
            await streams.WriteErrorLineAsync(ex.Message);
            return ExitCode.SettingsServerRefused;
        }
        catch (SettingsServerUnavailableException ex)
        {
            await streams.WriteErrorLineAsync(ex.Message);
            return ExitCode.SettingsServerUnavailable;
        }
        catch (SettingsServerErrorException ex)
        {
            // Same unprefixed-message rule as the two exceptions above: ServerSettingsStore already
            // stamped "ai-raccoon: ", so this goes to stderr as-is rather than through
            // CliFailureFormatting, which would double it.
            await streams.WriteErrorLineAsync(ex.Message);
            return ExitCode.SettingsServerError;
        }
        catch (Exception ex)
        {
            await streams.WriteErrorLineAsync(CliFailureFormatting.Format(ex, cliInput.ServerConfig.Options.DataRoot));
            return ExitCode.InvalidArgument;
        }
    }
}
