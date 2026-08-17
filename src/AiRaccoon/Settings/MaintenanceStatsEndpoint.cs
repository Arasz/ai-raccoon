using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane maintenance-stats resource (ADR-0075 amendment): `settings
///     maintenance list` reaches the server entirely — read-only, unconditional, no --apply gate —
///     so the CLI never opens the bank for it. Guarded by <see cref="McpTokenGate" /> like every
///     other route.
/// </summary>
internal static partial class MaintenanceStatsEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapMaintenanceStats()
        {
            var logger = webApplication.Logger;

            webApplication.MapGet(MaintenanceStatsProtocol.Path,
                async (IMaintenanceStatsStore store, CancellationToken ctx) =>
                {
                    var stats = await store.GetStatsAsync(ctx);
                    Log.StatsServed(logger);
                    return Results.Ok(stats);
                });
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 684, Level = LogLevel.Debug, Message = "ai-raccoon: maintenance stats served")]
        public static partial void StatsServed(ILogger logger);
    }
}
