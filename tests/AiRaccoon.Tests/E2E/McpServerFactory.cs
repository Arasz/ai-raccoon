using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Boots the real HTTP MCP server (MCP_TRANSPORT=http) in-process over
///     WebApplicationFactory and exposes an MCP client bound to it. Each instance
///     gets its own temp data root; provisioning reuses the developer's already
///     provisioned extensions when present, exactly like the store integration tests.
///     The server reads MCP_TRANSPORT / AIRACCOON_DATA_ROOT from the environment, so
///     those are set before the host starts and restored on dispose — E2E tests must
///     therefore run in a non-parallel collection (see E2ETestCollection).
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    private readonly string? _previousDataRoot;
    private readonly string? _previousTransport;
    private bool _disposed;

    public McpServerFactory()
    {
        _previousTransport = Environment.GetEnvironmentVariable("MCP_TRANSPORT");
        _previousDataRoot = Environment.GetEnvironmentVariable("AIRACCOON_DATA_ROOT");
        Environment.SetEnvironmentVariable("MCP_TRANSPORT", "http");
        Environment.SetEnvironmentVariable("AIRACCOON_DATA_ROOT", DataRoot);
        TryProvisionNativeExtensions();
    }

    /// <summary>The temp data root the server instance writes into.</summary>
    public string DataRoot { get; } = CreateTempRoot();

    /// <summary>True when the host RID's native extensions are available (copied into this instance's data root).</summary>
    public bool HasNativeExtensions => Directory.Exists(Path.Combine(DataRoot, "extensions", RuntimeInformation.RuntimeIdentifier));

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
        try
        {
            Directory.Delete(DataRoot, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    /// <summary>
    ///     Copies the host RID's already-provisioned native modules (~/.ai-raccoon/extensions/&lt;rid&gt;)
    ///     into this instance's data root so the real sqlite-memory/vector/cloudsync binaries load.
    ///     When nothing is provisioned, E2E tests must Assert.Skip (they cannot fake the extension).
    /// </summary>
    private void TryProvisionNativeExtensions()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var source = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ai-raccoon", "extensions", rid);
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = Path.Combine(DataRoot, "extensions", rid);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        }
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
