using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Projects;
using AiRaccoon.Infrastructure.Embedding;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane settings resource (ADR-0075): the server is the only process that
///     writes the bank, so the CLI reaches settings through here rather than opening it. Guarded by
///     <see cref="McpTokenGate" /> like every other route — it carries sync credentials and the
///     embedding API key.
/// </summary>
internal static partial class SettingsEndpoint
{
    /// <summary>The one refusal message #592 ships: endpoint throw → 409 body → client exception → stderr, verbatim.</summary>
    private const string ModelResetRefusedMessage =
        "ai-raccoon: model reset refused: a model migration is in progress — every MCP tool call is refused until it finishes; nothing was deleted";

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

                    var resolved = Resolve(key!);
                    var value = await store.GetSettingAsync(resolved, ctx);
                    Log.KeyRead(logger, resolved, value is not null);
                    return value is null ? Results.NotFound() : Results.Ok(new SettingValue(value));
                });

            webApplication.MapPut(SettingsProtocol.Path,
                async (SettingWrite write, ISettingsStore store, IProjectRegistrationGuard registration, CancellationToken ctx) =>
                {
                    if (string.IsNullOrWhiteSpace(write.Key))
                    {
                        return Results.BadRequest("ai-raccoon: a settings write needs a key");
                    }

                    var key = write.Key;
                    if (ProjectSettingsKeys.TryGetProjectId(write.Key, out var owner))
                    {
                        if (string.IsNullOrWhiteSpace(owner))
                        {
                            return Results.BadRequest($"ai-raccoon: the settings key '{write.Key}' names no project");
                        }

                        var folded = ProjectIdAliasMap.Default.Apply(owner);
                        key = ProjectSettingsKeys.WithProjectId(write.Key, folded.ProjectId);
                        if (await RefuseProjectAsync(key, write.Value, folded, store, registration, ctx) is { } refusal)
                        {
                            return Refused(refusal);
                        }
                    }

                    await store.SetSettingAsync(key, write.Value, ctx);
                    Log.KeyWritten(logger, key);
                    return Results.NoContent();
                });

            // Absent is the same as removed, matching every settings handler that deletes a row.
            webApplication.MapDelete(SettingsProtocol.Path,
                async (string? key, ISettingsStore store, IModelMigrationStore migrations, CancellationToken ctx) =>
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        return Results.BadRequest("ai-raccoon: a settings delete needs ?key=");
                    }

                    try
                    {
                        // ADR-0076 (#592): deleting embedding.provider while a migration is open
                        // would strand the outbox — the one key only ModelResetAsync deletes. This
                        // route is the ADR-0075 write choke point; a future server-side
                        // IMemoryStore.DeleteSettingAsync caller would bypass this guard (R1 F12).
                        if (key == EmbeddingSettingsKeys.Provider && await migrations.HasOpenModelMigrationAsync(ctx))
                        {
                            throw new ModelMigrationInProgressException(ModelResetRefusedMessage);
                        }

                        await store.DeleteSettingAsync(key, ctx);
                        Log.KeyDeleted(logger, key);
                        return Results.NoContent();
                    }
                    catch (ModelMigrationInProgressException ex)
                    {
                        // Plain text, not Results.Conflict(ex.Message): the 409 body must be the
                        // frozen message itself so it reaches stderr verbatim (Conflict would
                        // JSON-quote it).
                        return Results.Text(ex.Message, "text/plain", statusCode: StatusCodes.Status409Conflict);
                    }
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

                    // #708: the CLI refuses an unusable base-url before persisting anything (ADR-0107
                    // PC.1, #700), but a direct (non-CLI) caller reaches this route unchecked — refuse
                    // it here too, before the outbox commits or a migration opens.
                    if (request.BaseUrl is not null && !BaseUrlValidation.IsUsableHttpUrl(request.BaseUrl))
                    {
                        return Results.BadRequest(
                            $"ai-raccoon: '{request.BaseUrl}' is not a usable absolute http(s) URL; " +
                            "pass a full URL such as https://api.example.com/v1");
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

            // §3.3 D-E9: commits the settings rows + code_entries invalidation in one transaction
            // and returns — no outbox, no relay, no ToolGate interaction. The code-reindex
            // maintenance job drains the pending rows separately.
            webApplication.MapPost(SettingsProtocol.ModelCodePath,
                async (ModelCodeActivationRequest request, ICodeEngineStore store, CancellationToken ctx) =>
                {
                    if (string.IsNullOrWhiteSpace(request.Directory))
                    {
                        return Results.BadRequest("ai-raccoon: a code model activation needs a directory");
                    }

                    try
                    {
                        var config = await store.ActivateCodeEngineAsync(request.Directory, ctx);
                        Log.CodeEngineActivated(logger, config.Engine);
                        return Results.Ok(new ModelCodeActivationResponse(config.Model, config.Engine));
                    }
                    catch (CodeEngineActivationRefusedException ex)
                    {
                        return Results.BadRequest(ex.Message);
                    }
                });
        }
    }

    /// <summary>A per-project key under its alias winner's id; any other key, or a blank owner, unchanged.</summary>
    private static string Resolve(string key) =>
        ProjectSettingsKeys.TryGetProjectId(key, out var owner) && !string.IsNullOrWhiteSpace(owner)
            ? ProjectSettingsKeys.WithProjectId(key, ProjectIdAliasMap.Default.Apply(owner).ProjectId)
            : key;

    /// <summary>
    ///     The refusal for a per-project write, or null to write it. A retired id is refused; a scope
    ///     list that only loses paths is allowed; otherwise the id must pass the read form of the
    ///     registration check, which never registers it.
    /// </summary>
    private static async Task<string?> RefuseProjectAsync(string key, string value, FoldedProjectId folded,
        ISettingsStore store, IProjectRegistrationGuard registration, CancellationToken ctx)
    {
        if (folded.Dropped)
        {
            return new RetiredProjectException(folded.ProjectId).Message;
        }

        if (IsScopeList(key) && IngestScopeList.IsSubset(await store.GetSettingAsync(key, ctx), value))
        {
            return null;
        }

        try
        {
            await registration.EnsureAsync(folded.ProjectId, AccessRequirement.Read, ctx);
            return null;
        }
        catch (UnregisteredProjectException ex)
        {
            return ex.Message;
        }
    }

    private static bool IsScopeList(string key) =>
        key.StartsWith("ingest.scope.", StringComparison.Ordinal)
        || key.StartsWith(IngestScopeKeys.LegacyScopePrefix, StringComparison.Ordinal);

    /// <summary>Plain text, so the CLI writes the reason to stderr verbatim.</summary>
    private static IResult Refused(string reason) =>
        Results.Text($"ai-raccoon: settings write refused: {reason}", "text/plain", statusCode: StatusCodes.Status409Conflict);

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

        [LoggerMessage(EventId = 675, Level = LogLevel.Information,
            Message = "ai-raccoon: code engine activation to {Engine} committed; the code-reindex maintenance job will drain the pending rows")]
        public static partial void CodeEngineActivated(ILogger logger, string engine);
    }
}
