using AiRaccoon.Core.Access;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Boots the real HTTP MCP server in-process over WebApplicationFactory and exposes an
///     MCP client bound to it. Each instance gets its own temp data root. Launch identity
///     flows through the real entry point's args: WebApplicationFactory renders
///     UseSetting values as --key=value args (transport/data-root/install-scope), which the
///     parse-first Program consumes like any user invocation. The global access mode is
///     runtime config (single channel), so the factory seeds access.mode.global=full the
///     same way `ai-raccoon access default set full` would — before the first bank open.
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    private readonly InstallScope _scope;
    private bool _disposed;

    public McpServerFactory(InstallScope scope = InstallScope.User)
    {
        _scope = scope;
    }

    /// <summary>The temp data root the server instance writes into.</summary>
    public string DataRoot { get; } = CreateTempRoot();

    public async Task<McpClient> CreateClientAsync()
    {
        // full mode so the workspace consolidate/discard E2E flows keep working under FR-NM-2 (see docs/work/features-native-memory/native-memory.feature)
        // (the settings row is read per call by the access guard).
        await SeedGlobalAccessModeAsync();
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

    private async Task SeedGlobalAccessModeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = DataRoot, Scope = _scope };
        var store = new SqliteMemoryStore(
            new SqliteConnectionFactory(options,
                new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                    [new EnvEncryptionKeyProvider()])),
            TimeProvider.System, new TokenizerChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(AccessMode.Full));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // WebApplicationFactory turns UseSetting into entry-point args (--key=value), the
        // launch-identity channel the server reads after the env-merge removal.
        builder.UseSetting("transport", "http");
        builder.UseSetting("data-root", DataRoot);
        if (_scope == InstallScope.Project)
        {
            builder.UseSetting("install-scope", "project");
        }

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
        try
        {
            Directory.Delete(DataRoot, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot();
}
