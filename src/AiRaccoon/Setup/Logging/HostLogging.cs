using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Setup.Logging;

/// <summary>
///     Single logging configuration point for the web host (owner ruling 2026-08-09, D6):
///     quiet decides the destination — a file, never stdout/stderr — before anything decides
///     level. The web host is the sole surviving host shape, so the ASP.NET/MCP-server
///     chatter floor always applies.
/// </summary>
internal static class HostLogging
{
    internal static void Configure(ILoggingBuilder loggingBuilder, InfrastructureOptions options)
    {
        // Sole surviving host is the web host: the floors always apply.
        loggingBuilder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        loggingBuilder.AddFilter("ModelContextProtocol", LogLevel.Warning);

        if (options.Quiet)
        {
            QuietLogging.Configure(loggingBuilder, options);
            return;
        }

        loggingBuilder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    }
}
