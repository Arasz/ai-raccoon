using System.Collections;
using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Watchdog;
using AiRaccoon.Observability;
using AiRaccoon.Prompts;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Logging;
using AiRaccoon.Tools;
using CommunityToolkit.Diagnostics;
using ModelContextProtocol.Server;
using DotNext.Collections.Generic;
using ShutdownEndpoint = AiRaccoon.Hosting.Node.ShutdownEndpoint;

namespace AiRaccoon.Setup;

/// <summary>
///     Wires the MCP server for resolved transport set: a plain app host for stdio-only
///     (no web server, no HTTP bind), a web host for HTTP/S, and a web host with stdio
///     attached for a combined set. https is declared but unsupported (warning only).
/// </summary>
internal static partial class McpServerSetup
{
    /// <summary>
    ///     Flips ASP.NET Core 10's default off so the hosting request Activity carries OTel
    ///     HTTP semconv tags — what ASP.NET Core 11 does by default (docs/adr/0021).
    /// </summary>
    private const string AspNetCoreHostingOpenTelemetryDataSwitch =
        "Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData";

    /// <summary>Creates the server host for the config's single transport.</summary>
    internal static IHost CreateServerHost(ServerConfig config) => CreateServerHost(config, [config.Transport], TimeProvider.System);

    /// <summary>
    ///     Creates the server host for transport set: stdio-only launches a plain app host; any
    ///     HTTP/S presence uses a web host on the configured port, with stdio attached too when
    ///     both are selected. timeProvider is a test seam for the watchdog tests; null keeps TimeProvider.System.
    /// </summary>
    internal static IHost CreateServerHost(ServerConfig config, IReadOnlyCollection<McpTransport> transports, TimeProvider timeProvider)
    {
        if (transports.Count == 1 && transports.Contains(McpTransport.Stdio))
        {
            return CreateAppHost(config);
        }

        return CreateWebHost(config, transports, timeProvider);
    }

    public static WebApplication CreateWebHost(ServerConfig serverConfig)
    {
        if (serverConfig.Transport == McpTransport.Stdio)
        {
            ThrowHelper.ThrowArgumentException<McpTransport>($"{McpTransport.Stdio} can't be used to create a WebHost");
        }

        return CreateWebHost(serverConfig, [serverConfig.Transport], TimeProvider.System);
    }

    private static IHost CreateAppHost(ServerConfig config)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        var mcpTransport = IReadOnlyList<McpTransport>.Singleton(McpTransport.Stdio);
        HostLogging.Configure(builder.Logging, mcpTransport, config.Options);
        builder.Services.RegisterMemoryServices(config.Options, mcpTransport);
        builder.Services
            .AddMcpServer()
            .ConfigureMcpTransport(mcpTransport)
            .WithTools<MemoryTools>()
            .WithTools<CodeTools>()
            .WithTools<ShareTools>()
            .WithTools<WorkspaceTools>()
            .WithTools<SweepTools>()
            .WithTools<SyncTools>()
            .WithTools<PromotionTools>()
            .WithTools<WatchTools>()
            .WithTools<QualityTools>()
            .WithTools<PerformanceTools>()
            .WithTools<ProjectTools>()
            .WithPrompts<MemoryPrompts>();
        return builder.Build();
    }

    private static WebApplication CreateWebHost(ServerConfig serverConfig, IReadOnlyCollection<McpTransport> transports, TimeProvider timeProvider)
    {
        if (OtlpExportState.Resolve().Enabled)
        {
            AppContext.SetSwitch(AspNetCoreHostingOpenTelemetryDataSwitch, false);
        }

        var builder = WebApplication.CreateBuilder([]);
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .Select(entry => new KeyValuePair<string, string?>(entry.Key.ToString() ?? "", entry.Value?.ToString()))
                .Where(kv => !string.IsNullOrEmpty(kv.Key) && kv.Key.StartsWith("OTEL_", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key, kv => kv.Value));

        builder.Services.RegisterMemoryServices(serverConfig.Options, transports);
        builder.Services.AddOtlpExport(serverConfig.Options);
        builder.Services.AddSingleton(timeProvider);
        HostLogging.Configure(builder.Logging, transports, serverConfig.Options);

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, serverConfig.Port));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownEndpoint.DrainWindow);
        builder.Services.RegisterWatchdogServices(serverConfig);

        builder.ConfigureMcpServer(transports);
        return builder.Build().ConfigureMcpEndpoints(serverConfig, transports);
    }

    extension(WebApplication webApplication)
    {
        private WebApplication ConfigureMcpEndpoints(ServerConfig config, IReadOnlyCollection<McpTransport> transports)
        {
            if (transports.Contains(McpTransport.Https))
            {
                Log.HttpsTransportNotSupported(webApplication.Logger);
            }

            var gated = config.McpToken;
            if (gated != null)
            {
                webApplication.UseMiddleware<McpTokenGate>(gated, new McpTokenFile(config.Options.DataRoot).Path);
            }

            if (config.IdleTimeout > TimeSpan.Zero)
            {
                webApplication.UseMiddleware<McpActivityMiddleware>();
            }

            if (!transports.Contains(McpTransport.Http))
            {
                return webApplication;
            }

            webApplication.MapMcp("/mcp");
            webApplication.MapObservability();
            if (gated is not null)
            {
                webApplication.MapShutdown();
                // Carries sync credentials and the embedding API key, so it is mapped only on a
                // token-guarded host, like /shutdown.
                webApplication.MapSettings();
                // ADR-0075 amendment: repair reaches the server entirely, same token-guarded host.
                webApplication.MapRepair();
                // ADR-0075 amendment: extract prune and settings maintenance list reach the server
                // entirely too, same token-guarded host.
                webApplication.MapPromotionQueuePrune();
                webApplication.MapMaintenanceStats();
                // ADR-0075 amendment: noise entries and watch registered reach the server entirely too.
                webApplication.MapNoiseSummary();
                webApplication.MapWatchRegistered();
            }

            return webApplication;
        }
    }

    extension(WebApplicationBuilder webApplicationBuilder)
    {
        private void ConfigureMcpServer(IReadOnlyCollection<McpTransport> transports) =>
            webApplicationBuilder
                .Services
                .AddMcpServer()
                .ConfigureMcpTransport(transports)
                .WithTools<MemoryTools>()
                .WithTools<CodeTools>()
                .WithTools<ShareTools>()
                .WithTools<WorkspaceTools>()
                .WithTools<SweepTools>()
                .WithTools<SyncTools>()
                .WithTools<PromotionTools>()
                .WithTools<WatchTools>()
                .WithTools<QualityTools>()
                .WithTools<PerformanceTools>()
                .WithTools<ProjectTools>()
                .WithPrompts<MemoryPrompts>();
    }

    extension(IMcpServerBuilder mcpServerBuilder)
    {
        private IMcpServerBuilder ConfigureMcpTransport(IReadOnlyCollection<McpTransport> selectedTransports)
        {
            // Both hosts (stdio AppHost and the web host) route through here, so the instructions
            // are set once rather than copied into each AddMcpServer call.
            mcpServerBuilder.Services.Configure<McpServerOptions>(
                options => options.ServerInstructions = McpServerInstructions.Text);
            mcpServerBuilder = mcpServerBuilder.WithRequestFilters(f => f
                .AddCallToolFilter(ToolRefusals.Filter)
                .AddCallToolFilter(ToolTelemetry.Filter));

            if (selectedTransports.Count == 0)
            {
                return mcpServerBuilder.WithStdioServerTransport();
            }

            foreach (var selectedTransport in selectedTransports)
            {
                mcpServerBuilder = selectedTransport switch
                {
                    McpTransport.Stdio => mcpServerBuilder.WithStdioServerTransport(),
                    McpTransport.Http => mcpServerBuilder.HandleHttpTransport(),
                    McpTransport.Https => mcpServerBuilder.HandleHttpsTransport(),
                    _ => mcpServerBuilder
                };
            }

            return mcpServerBuilder;
        }

        private IMcpServerBuilder HandleHttpTransport() => mcpServerBuilder.WithHttpTransport(options => { options.Stateless = true; });

        private IMcpServerBuilder HandleHttpsTransport() => mcpServerBuilder;
    }

    internal static partial class Log
    {
        [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "ai-raccoon: https transport is not supported")]
        public static partial void HttpsTransportNotSupported(ILogger logger);
    }
}
