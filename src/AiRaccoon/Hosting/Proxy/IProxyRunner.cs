using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Hosting.Proxy;

public interface IProxyRunner
{
    /// <summary>
    ///     Relays stdio traffic to the backend until the client disconnects; loud on failure, never
    ///     falls back. processPath is this process's own path (Environment.ProcessPath in production):
    ///     the backend it may auto-start is this same binary run as `serve`.
    /// </summary>
    Task<int> RunAsync(ServerConfig serverConfig, StandardStreams streams, string? processPath, CancellationToken ctx);
}
