using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AiRaccoon.Tests.E2E;

/// <summary>Speaks MCP to the built ai-raccoon binary over a real stdio pipe, as an MCP client does.</summary>
public static class AiRaccoonProcess
{
    /// <summary>Throws naming the expected path when the build output is missing.</summary>
    public static string Executable => RaccoonProcess.Executable;

    public static Task<McpClient> ConnectAsync(string[] arguments, CancellationToken cancellationToken) =>
        ConnectAsync(arguments, null, cancellationToken);

    public static Task<McpClient> ConnectAsync(string[] arguments, McpClientOptions? clientOptions,
        CancellationToken cancellationToken) =>
        McpClient.CreateAsync(
            new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "ai-raccoon-under-test",
                    Command = Executable,
                    Arguments = arguments,
                    ShutdownTimeout = TimeSpan.FromSeconds(10)
                },
                NullLoggerFactory.Instance),
            clientOptions, cancellationToken: cancellationToken);
}
