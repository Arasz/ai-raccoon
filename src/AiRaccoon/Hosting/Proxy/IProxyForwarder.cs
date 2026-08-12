using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AiRaccoon.Hosting.Proxy;

public interface IProxyForwarder
{
    /// <summary>
    ///     Relays requests and notifications to the backend, suppressing the local handlers; any other
    ///     message kind falls through to them. A lost connection re-acquires and retries once.
    ///     <paramref name="open" /> takes the revision the client asked for, or null for the SDK's choice.
    /// </summary>
    McpMessageFilter Create(McpSession backend, IBackendSessions backendSessions);
}
