using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Hosting.Node;

namespace AiRaccoon.Settings;

/// <summary>
///     Serves the control-plane noise-summary resource (ADR-0075 amendment): `noise entries` reaches
///     the server entirely — read-only, unconditional, no --apply gate — so the CLI never opens the
///     bank for it. Guarded by <see cref="McpTokenGate" /> like every other route.
/// </summary>
internal static partial class NoiseSummaryEndpoint
{
    extension(WebApplication webApplication)
    {
        internal void MapNoiseSummary()
        {
            var logger = webApplication.Logger;

            webApplication.MapGet(NoiseSummaryProtocol.Path,
                async (INoiseSummaryStore store, CancellationToken ctx) =>
                {
                    var summary = await store.SummarizeAsync(ctx);
                    Log.SummaryServed(logger);
                    return Results.Ok(summary);
                });
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 685, Level = LogLevel.Debug, Message = "ai-raccoon: noise summary served")]
        public static partial void SummaryServed(ILogger logger);
    }
}
