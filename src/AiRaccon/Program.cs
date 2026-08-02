using AiRaccon.Core.Memory;
using AiRaccon.Infrastructure.Degradation;
using AiRaccon.Infrastructure.Options;
using AiRaccon.Infrastructure.Provisioning;
using AiRaccon.Infrastructure.Sqlite;
using AiRaccon.Infrastructure.Sync;
using AiRaccon.Infrastructure.Workspace;
using AiRaccon.Prompts;
using AiRaccon.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Transport selection: the "http" launch profile sets MCP_TRANSPORT=http to run the
// Streamable HTTP transport; anything else (default) uses stdio, which is what MCP
// clients expect when launching the server as a subprocess.
if (McpTransportSelector.UseHttp(Environment.GetEnvironmentVariable("MCP_TRANSPORT")))
{
    var builder = WebApplication.CreateBuilder(args);

    // Add the MCP services: the transport to use (http) and the tools to register.
    builder.Services
        .AddMcpServer()
        .WithHttpTransport(options =>
        {
            // Stateless mode is recommended for servers that don't need
            // server-to-client requests like sampling or elicitation.
            options.Stateless = true;
        })
        .WithTools<MemoryTools>()
        .WithPrompts<MemoryPrompts>();

    RegisterMemoryServices(builder.Services);

    var app = builder.Build();
    app.MapMcp("/mcp");
    app.Run();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configure all logs to go to stderr (stdout is used for the MCP protocol messages).
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    // Add the MCP services: the transport to use (stdio) and the tools to register.
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithTools<MemoryTools>()
        .WithPrompts<MemoryPrompts>();

    RegisterMemoryServices(builder.Services);

    await builder.Build().RunAsync();
}

static void RegisterMemoryServices(IServiceCollection services)
{
    // Options come from environment variables only — never hardcoded credentials.
    var scope = string.Equals(
        Environment.GetEnvironmentVariable("AIRACCON_INSTALL_SCOPE"), "project", StringComparison.OrdinalIgnoreCase)
        ? InstallScope.Project
        : InstallScope.User;

    var options = new InfrastructureOptions
    {
        DataRoot = InfrastructureOptions.DefaultDataRoot(),
        Scope = scope,
        Sync = new SyncOptions
        {
            ManagedDatabaseId = Environment.GetEnvironmentVariable("AIRACCON_SQLITECLOUD_DB_ID"),
            ApiKey = Environment.GetEnvironmentVariable("AIRACCON_SQLITECLOUD_API_KEY"),
        },
    };

    // Provision native extensions on first run (FR-MEM-1.19): download + verify the pinned
    // vector/memory/cloudsync modules for the host RID before any connection opens them.
    ProvisionExtensions(options);

    services.AddSingleton(options);
    services.AddSingleton(sp => new SqliteConnectionFactory(
        sp.GetRequiredService<InfrastructureOptions>(),
        loadCloudSync: true));
    services.AddSingleton<IMemoryStore, SqliteMemoryStore>();
    services.AddSingleton<MetaStore>();
    services.AddSingleton<ICloudSyncConnectionFactory, CloudSyncConnectionFactory>();
    services.AddSingleton<SyncService>();
    services.AddSingleton<WorkspaceService>();
    services.AddSingleton<SweepService>();
}

static void ProvisionExtensions(InfrastructureOptions options)
{
    try
    {
        using var http = new HttpClient();
        var provisioner = new ExtensionProvisioner(
            options.DataRoot, options.Rid, http, ExtensionManifest.Sha256, includeCloudSync: true);
        provisioner.EnsureProvisionedAsync().GetAwaiter().GetResult();
    }
    catch (Exception exception)
    {
        // Do not crash the server on provisioning failure — the first tool call that opens the
        // bank will surface the precise missing-module error. Log to stderr (stdio-safe).
        Console.Error.WriteLine($"ai-raccon: native extension provisioning failed: {exception.Message}");
    }
}
