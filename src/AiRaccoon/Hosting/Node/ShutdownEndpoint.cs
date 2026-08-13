namespace AiRaccoon.Hosting.Node;

/// <summary>
///     Maps POST /shutdown (ADR-0022): answers first, then stops the host gracefully. Mapped only
///     on a token-guarded host, and guarded by <see cref="McpTokenGate" /> like /mcp.
/// </summary>
internal static partial class ShutdownEndpoint
{
    public const string Path = "/shutdown";

    /// <summary>
    ///     The host's whole StopAsync budget (HostOptions.ShutdownTimeout), shared by every
    ///     hosted service and by in-flight requests — not a per-request guarantee. 10s, down from the
    ///     30s framework default (ADR-0022).
    /// </summary>
    public static readonly TimeSpan DrainWindow = TimeSpan.FromSeconds(10);

    extension(WebApplication webApplication)
    {
        internal void MapShutdown()
        {
            var logger = webApplication.Logger;
            webApplication.MapPost(Path, (HttpContext context, IHostApplicationLifetime lifetime) =>
            {
                // Stopped only after the response is flushed: the caller has to learn it landed.
                context.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
                Log.ShutdownRequested(logger, Environment.ProcessId, DrainWindow);
                return TypedResults.Accepted((string?)null);
            });
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 660, Level = LogLevel.Information,
            Message = "ai-raccoon: shutdown requested over /shutdown; stopping pid {Pid}, draining for up to {DrainWindow}")]
        public static partial void ShutdownRequested(ILogger logger, int pid, TimeSpan drainWindow);
    }
}
