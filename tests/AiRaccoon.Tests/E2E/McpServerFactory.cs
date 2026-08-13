using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Boots the real HTTP MCP server in-process via WebApplicationFactory and exposes an MCP
///     client bound to it, each instance under its own temp data root. Launch identity flows
///     through entry-point args (docs/plans/cli-args-parsing.md); access mode defaults to full before the first bank open.
/// </summary>
public sealed class McpServerFactory : WebApplicationFactory<Program>
{
    private readonly Action<IServiceCollection>? _configureAdditionalServices;
    private readonly InstallScope _scope;
    private bool _disposed;

    /// <summary>configureAdditionalServices is a test seam (e.g. chaining AddInMemoryExporter onto
    /// the OTel builder the real host already configures) — production boot never passes it.</summary>
    public McpServerFactory(InstallScope scope = InstallScope.User, Action<IServiceCollection>? configureAdditionalServices = null)
    {
        _scope = scope;
        _configureAdditionalServices = configureAdditionalServices;
    }

    /// <summary>The temp data root the server instance writes into.</summary>
    public string DataRoot { get; } = CreateTempRoot();

    public async Task<McpClient> CreateClientAsync()
    {
        // Full access mode keeps the workspace consolidate/discard E2E flows working under FR-NM-2
        // (docs/work/features-native-memory/native-memory.feature); the settings row is read per call.
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
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), TimeProvider.System, new EmbeddingService());
        await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(AccessMode.Full));

        // Ingest is contained by the declared scope, so the E2E server is configured with one
        // just as a real deployment is; the surface tests ingest from temp paths.
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal,
            IngestScopeKeys.Serialize([Path.GetTempPath(), DataRoot]));
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
        if (_configureAdditionalServices is not null)
        {
            builder.ConfigureServices(_configureAdditionalServices);
        }
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
