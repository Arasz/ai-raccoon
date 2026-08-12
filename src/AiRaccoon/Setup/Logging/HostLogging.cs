using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup.Logging;

/// <summary>
///     Single logging configuration point for every host shape (owner ruling 2026-08-09, D6):
///     quiet decides the destination — a file, never stdout/stderr — before anything decides
///     level; transport decides which per-category floors apply, since the ASP.NET/MCP-server
///     chatter floor only makes sense where those categories exist.
/// </summary>
internal static class HostLogging
{
    internal static void Configure(ILoggingBuilder loggingBuilder, IReadOnlyCollection<McpTransport> transports,
        InfrastructureOptions options)
    {
        if (transports.Contains(McpTransport.Http) || transports.Contains(McpTransport.Https))
        {
            loggingBuilder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
            loggingBuilder.AddFilter("ModelContextProtocol", LogLevel.Warning);
        }

        if (options.Quiet)
        {
            QuietLogging.Configure(loggingBuilder, options);
            return;
        }

        loggingBuilder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }
}
