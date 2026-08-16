using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane settings resource (ADR-0075): the server is the only process that
///     writes the bank, so the CLI reaches settings through here rather than opening it. Guarded by
///     <see cref="McpTokenGate" /> like every other route — it carries sync credentials and the
///     embedding API key.
/// </summary>
internal static partial class SettingsEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapSettings()
        {
            var logger = webApplication.Logger;

            webApplication.MapGet(SettingsProtocol.Path,
                async (string? key, string? prefix, ISettingsStore store, CancellationToken ctx) =>
                {
                    if (string.IsNullOrEmpty(key) == string.IsNullOrEmpty(prefix))
                    {
                        return Results.BadRequest("ai-raccoon: pass exactly one of ?key= or ?prefix=");
                    }

                    if (!string.IsNullOrEmpty(prefix))
                    {
                        var rows = await store.GetSettingsByPrefixAsync(prefix, ctx);
                        Log.PrefixRead(logger, prefix, rows.Count);
                        return Results.Ok(new SettingRows(rows));
                    }

                    var value = await store.GetSettingAsync(key!, ctx);
                    Log.KeyRead(logger, key!, value is not null);
                    return value is null ? Results.NotFound() : Results.Ok(new SettingValue(value));
                });

            webApplication.MapPut(SettingsProtocol.Path,
                async (SettingWrite write, ISettingsStore store, CancellationToken ctx) =>
                {
                    if (string.IsNullOrWhiteSpace(write.Key))
                    {
                        return Results.BadRequest("ai-raccoon: a settings write needs a key");
                    }

                    await store.SetSettingAsync(write.Key, write.Value, ctx);
                    Log.KeyWritten(logger, write.Key);
                    return Results.NoContent();
                });

            // Absent is the same as removed, matching every settings handler that deletes a row.
            webApplication.MapDelete(SettingsProtocol.Path,
                async (string? key, ISettingsStore store, CancellationToken ctx) =>
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        return Results.BadRequest("ai-raccoon: a settings delete needs ?key=");
                    }

                    await store.DeleteSettingAsync(key, ctx);
                    Log.KeyDeleted(logger, key);
                    return Results.NoContent();
                });

            // ADR-0076: commits the outbox transaction and returns — no inline re-embed, no
            // progress. The maintenance loop's on-demand relay (ModelMigrationJob) drains it.
            webApplication.MapPost(SettingsProtocol.ModelPath,
                async (ModelMigrationRequest request, IModelMigrationStore store, CancellationToken ctx) =>
                {
                    if (string.IsNullOrWhiteSpace(request.Provider))
                    {
                        return Results.BadRequest("ai-raccoon: a model migration needs a provider");
                    }

                    try
                    {
                        var config = await store.StartModelMigrationAsync(request.Provider, request.Model,
                            request.BaseUrl, ctx);
                        Log.ModelMigrationStarted(logger, config.Engine);
                        return Results.Ok(new ModelMigrationResponse(config.Provider, config.Model, config.Engine));
                    }
                    catch (ModelMigrationInProgressException ex)
                    {
                        return Results.Conflict(ex.Message);
                    }
                });
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 670, Level = LogLevel.Debug, Message = "ai-raccoon: settings read {Key} (present {Present})")]
        public static partial void KeyRead(ILogger logger, string key, bool present);

        [LoggerMessage(EventId = 671, Level = LogLevel.Debug, Message = "ai-raccoon: settings read prefix {Prefix} ({Count} rows)")]
        public static partial void PrefixRead(ILogger logger, string prefix, int count);

        // The value is deliberately absent: sync credentials and the embedding API key go through here.
        [LoggerMessage(EventId = 672, Level = LogLevel.Information, Message = "ai-raccoon: settings wrote {Key}")]
        public static partial void KeyWritten(ILogger logger, string key);

        [LoggerMessage(EventId = 673, Level = LogLevel.Information, Message = "ai-raccoon: settings deleted {Key}")]
        public static partial void KeyDeleted(ILogger logger, string key);

        [LoggerMessage(EventId = 674, Level = LogLevel.Information,
            Message = "ai-raccoon: model migration to {Engine} committed; the maintenance loop's relay will finish it")]
        public static partial void ModelMigrationStarted(ILogger logger, string engine);
    }
}
