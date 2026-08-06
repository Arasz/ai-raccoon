using AiRaccoon.Setup;

namespace AiRaccoon;

public static partial class HostExtensions
{
    extension(IHost host)
    {
        /// <summary>
        ///     Runs the server host: start, report the bound http URL (only knowable after
        ///     start; 0 = random), then wait for shutdown. The http transport's only
        ///     discoverability channel for a random port.
        /// </summary>
        public async Task<int> RunAsync(ServerConfig config, CancellationToken cancellationToken = default)
        {
            await host.StartAsync(cancellationToken);
            if (config.Transport == McpTransport.Http && host is WebApplication web)
            {
                var urls = string.Join(", ", web.Urls.Select(url => $"{url.TrimEnd('/')}/mcp"));
                Log.HttpTransportListening(web.Logger, urls);
            }

            await host.WaitForShutdownAsync(cancellationToken);

            return ExitCode.Success;
        }
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "ai-raccoon: http transport listening on {Urls}")]
        public static partial void HttpTransportListening(ILogger logger, string urls);
    }
}
