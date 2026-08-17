using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane promotion-queue-prune resource (ADR-0075 amendment): `extract prune`
///     reaches the server entirely — a read-only report (GET) and, on --apply, an outbox request
///     (POST) — so the CLI never opens the bank for it. Guarded by <see cref="McpTokenGate" /> like
///     every other route.
/// </summary>
internal static partial class PromotionQueuePruneEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapPromotionQueuePrune()
        {
            var logger = webApplication.Logger;

            webApplication.MapGet(PromotionQueuePruneProtocol.Path,
                async (IPromotionQueuePruneStore store, CancellationToken ctx) =>
                {
                    var report = await store.ReportPruneOrphansAsync(ctx);
                    Log.ReportServed(logger, report.TotalOrphans);
                    return Results.Ok(report);
                });

            webApplication.MapPost(PromotionQueuePruneProtocol.Path,
                async (IPromotionQueuePruneStore store, CancellationToken ctx) =>
                {
                    await store.RequestPruneOrphansAsync(ctx);
                    Log.PruneRequested(logger);
                    return Results.NoContent();
                });
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 682, Level = LogLevel.Debug, Message = "ai-raccoon: promotion-queue-prune report served ({TotalOrphans} orphan(s))")]
        public static partial void ReportServed(ILogger logger, int totalOrphans);

        [LoggerMessage(EventId = 683, Level = LogLevel.Information, Message = "ai-raccoon: promotion-queue prune requested")]
        public static partial void PruneRequested(ILogger logger);
    }
}
