using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Boots the real HTTP MCP server (MCP_TRANSPORT=http) in-process over
///     WebApplicationFactory and exposes an MCP client bound to it. Each instance
///     gets its own temp data root. No sqliteai native extensions are needed —
///     vec0 comes from the NuGet package and FTS5 from the bundled SQLite.
///     The server reads MCP_TRANSPORT / AIRACCOON_DATA_ROOT from the environment, so
///     those are set before the host starts and restored on dispose — E2E tests must
///     therefore run in a non-parallel collection (see E2ETestCollection).
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    private readonly string? _previousDataRoot;
    private readonly string? _previousTransport;
    private readonly string? _previousAccessMode;
    private bool _disposed;

    public McpServerFactory()
    {
        _previousTransport = Environment.GetEnvironmentVariable("MCP_TRANSPORT");
        _previousDataRoot = Environment.GetEnvironmentVariable("AIRACCOON_DATA_ROOT");
        // full mode so the workspace consolidate/discard E2E flows keep working under FR-NM-2
        // (the seed at bank open turns this env value into the global access.mode setting).
        _previousAccessMode = Environment.GetEnvironmentVariable("AIRACCOON_ACCESS_MODE");
        Environment.SetEnvironmentVariable("MCP_TRANSPORT", "http");
        Environment.SetEnvironmentVariable("AIRACCOON_DATA_ROOT", DataRoot);
        Environment.SetEnvironmentVariable("AIRACCOON_ACCESS_MODE", "full");
    }

    /// <summary>The temp data root the server instance writes into.</summary>
    public string DataRoot { get; } = CreateTempRoot();

    public async Task<McpClient> CreateClientAsync()
    {
        var httpClient = CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "e2e-test",
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)),
            true);
        return await McpClient.CreateAsync(transport);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("MCP_TRANSPORT", _previousTransport);
        Environment.SetEnvironmentVariable("AIRACCOON_DATA_ROOT", _previousDataRoot);
        Environment.SetEnvironmentVariable("AIRACCOON_ACCESS_MODE", _previousAccessMode);
        try
        {
            Directory.Delete(DataRoot, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
