using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane repair resource (ADR-0075 amendment): `repair &lt;verb&gt;` reaches
///     the server entirely — a read-only report (GET) and, on --apply, an outbox request (POST) — so
///     the CLI never opens the bank for it. Guarded by <see cref="McpTokenGate" /> like every other
///     route.
/// </summary>
internal static partial class RepairEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapRepair()
        {
            var logger = webApplication.Logger;

            webApplication.MapGet(RepairProtocol.Path,
                async (string? kind, IRepairStore store, CancellationToken ctx) =>
                {
                    switch (kind)
                    {
                        case RepairKinds.Reingest:
                            var reingest = await store.ReportReingestAsync(ctx);
                            Log.ReportServed(logger, kind);
                            return Results.Ok(reingest);
                        case RepairKinds.ChunkIndex:
                            var chunkIndex = await store.ReportChunkIndexAsync(ctx);
                            Log.ReportServed(logger, kind);
                            return Results.Ok(chunkIndex);
                        case RepairKinds.ProjectIds:
                            var projectIds = await store.ReportProjectIdsAsync(ctx);
                            Log.ReportServed(logger, kind);
                            return Results.Ok(projectIds);
                        default:
                            return Results.BadRequest(
                                $"ai-raccoon: pass ?kind=reingest, ?kind=chunk-index or ?kind=project-ids (got '{kind}')");
                    }
                });

            webApplication.MapPost(RepairProtocol.Path,
                async (RepairRequest request, IRepairStore store, CancellationToken ctx) =>
                {
                    if (!TryParseKind(request.Kind, out var parsed))
                    {
                        return Results.BadRequest(
                            $"ai-raccoon: unknown repair kind '{request.Kind}' (expected reingest, chunk-index or project-ids)");
                    }

                    if (request.MapJson is not null && parsed == RepairKind.ProjectIds)
                    {
                        try
                        {
                            ProjectIdAliasMap.FromJson(request.MapJson);
                        }
                        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or NotSupportedException)
                        {
                            return Results.BadRequest(
                                $"ai-raccoon: project-ids map_json is not a valid alias map: {ex.Message}");
                        }
                    }

                    await store.RequestRepairAsync(parsed, ctx, request.MapJson);
                    Log.RepairRequested(logger, request.Kind);
                    return Results.NoContent();
                });
        }
    }

    private static bool TryParseKind(string? kind, out RepairKind parsed)
    {
        switch (kind)
        {
            case RepairKinds.Reingest:
                parsed = RepairKind.Reingest;
                return true;
            case RepairKinds.ChunkIndex:
                parsed = RepairKind.ChunkIndex;
                return true;
            case RepairKinds.ProjectIds:
                parsed = RepairKind.ProjectIds;
                return true;
            default:
                parsed = default;
                return false;
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 680, Level = LogLevel.Debug, Message = "ai-raccoon: repair report served ({Kind})")]
        public static partial void ReportServed(ILogger logger, string kind);

        [LoggerMessage(EventId = 681, Level = LogLevel.Information, Message = "ai-raccoon: repair requested ({Kind})")]
        public static partial void RepairRequested(ILogger logger, string kind);
    }
}
