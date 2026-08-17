using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Watch;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane watch-registered resource (ADR-0075 amendment): `watch registered`
///     reaches the server entirely — read-only, unconditional — so the CLI never opens the bank for
///     it. Guarded by <see cref="McpTokenGate" /> like every other route.
/// </summary>
internal static partial class WatchRegisteredEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapWatchRegistered()
        {
            var logger = webApplication.Logger;

            webApplication.MapGet(WatchRegisteredProtocol.Path,
                async (IWatchRegisteredStore store, CancellationToken ctx) =>
                {
                    var watches = await store.ListWatchesAsync(ctx);
                    Log.RegisteredServed(logger);
                    return Results.Ok(watches);
                });
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 686, Level = LogLevel.Debug, Message = "ai-raccoon: watch-registered list served")]
        public static partial void RegisteredServed(ILogger logger);
    }
}
