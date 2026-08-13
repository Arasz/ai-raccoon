using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Hosting.Proxy;

public interface IProxyRunner
{
    /// <summary>Relays stdio traffic to the backend until the client disconnects; loud on failure, never falls back.</summary>
    Task<int> RunAsync(ServerConfig serverConfig, StandardStreams streams, CancellationToken ctx);
}
