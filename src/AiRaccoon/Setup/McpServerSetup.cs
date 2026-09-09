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
using ModelContextProtocol.Server;
using ShutdownEndpoint = AiRaccoon.Hosting.Node.ShutdownEndpoint;

namespace AiRaccoon.Setup;

/// <summary>
///     Builds the MCP server's sole surviving host: a web host serving HTTP on the
///     configured loopback port. Bare launches never reach here — they proxy to the
///     backend (ADR-0020); stdio and https have no host path.
/// </summary>
internal static partial class McpServerSetup
{
    /// <summary>
    ///     Flips ASP.NET Core 10's default off so the hosting request Activity carries OTel
    ///     HTTP semconv tags — what ASP.NET Core 11 does by default (docs/adr/0021).
    /// </summary>
    private const string AspNetCoreHostingOpenTelemetryDataSwitch =
        "Microsoft.AspNetCore.Hosting.SuppressActivityOpenTelemetryData";

    /// <summary>Creates the server host for the config.</summary>
    internal static IHost CreateServerHost(ServerConfig config) => CreateServerHost(config, TimeProvider.System);

    /// <summary>
    ///     Creates the server host for the config. timeProvider is a test seam for the watchdog tests.
    /// </summary>
    internal static IHost CreateServerHost(ServerConfig config, TimeProvider timeProvider) => CreateWebHost(config, timeProvider);

    public static WebApplication CreateWebHost(ServerConfig serverConfig) => CreateWebHost(serverConfig, TimeProvider.System);

    private static WebApplication CreateWebHost(ServerConfig serverConfig, TimeProvider timeProvider)
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

        builder.Services.RegisterMemoryServices(serverConfig.Options);
        builder.Services.AddOtlpExport(serverConfig.Options);
        builder.Services.AddSingleton(timeProvider);
        HostLogging.Configure(builder.Logging, [serverConfig.Transport], serverConfig.Options);

        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, serverConfig.Port));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownEndpoint.DrainWindow);
        builder.Services.RegisterWatchdogServices(serverConfig);

        builder.ConfigureMcpServer();
        return builder.Build().ConfigureMcpEndpoints(serverConfig);
    }

    extension(WebApplication webApplication)
    {
        private WebApplication ConfigureMcpEndpoints(ServerConfig config)
        {
            var gated = config.McpToken;
            if (gated != null)
            {
                webApplication.UseMiddleware<McpTokenGate>(gated, new McpTokenFile(config.Options.DataRoot).Path);
            }

            if (config.IdleTimeout > TimeSpan.Zero)
            {
                webApplication.UseMiddleware<McpActivityMiddleware>();
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
        private void ConfigureMcpServer() =>
            webApplicationBuilder
                .Services
                .AddMcpServer()
                .ConfigureMcpTransport()
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
        private IMcpServerBuilder ConfigureMcpTransport()
        {
            // The web host is the sole surviving host shape, always HTTP, so the transport
            // wiring is unconditional rather than selected from a set.
            mcpServerBuilder.Services.Configure<McpServerOptions>(options => options.ServerInstructions = McpServerInstructions.Text);
            return mcpServerBuilder
                .WithRequestFilters(f => f
                    .AddCallToolFilter(ToolRefusals.Filter)
                    .AddCallToolFilter(ToolTelemetry.Filter))
                .HandleHttpTransport();
        }

        private IMcpServerBuilder HandleHttpTransport() => mcpServerBuilder.WithHttpTransport(options => { options.Stateless = true; });
    }
}
