using ModelContextProtocol.Client;

namespace AiRaccoon.Hosting.Proxy;

public interface IBackendSessions : IAsyncDisposable
{
    /// <summary>The endpoint the last successful acquire returned; empty until one succeeds.</summary>
    string Url { get; }
    Task<McpClient> OpenAsync(string? revision, CancellationToken ctx);
}
