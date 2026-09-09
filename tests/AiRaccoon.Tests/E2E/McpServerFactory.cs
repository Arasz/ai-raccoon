using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Boots the real HTTP MCP server in-process and exposes an MCP client bound to it, each
///     instance under its own temp data root. P2/ADR-0020: bare launches proxy out-of-process, so
///     the sole host is built directly (the same McpServerSetup.CreateWebHost production boots)
///     on an ephemeral loopback port, and the client dials its bound URL over real HTTP — no
///     WebApplicationFactory, no TestServer. Access mode defaults to full before the first bank open.
/// </summary>
public sealed class McpServerFactory : IDisposable, IAsyncDisposable
{
    private readonly InstallScope _scope;
    private WebApplication? _app;
    private bool _disposed;

    public McpServerFactory(InstallScope scope = InstallScope.User)
    {
        _scope = scope;
    }

    /// <summary>The temp data root the server instance writes into.</summary>
    public string DataRoot { get; } = CreateTempRoot();

    /// <summary>The live server's services (ForceFlush probes, log providers).</summary>
    public IServiceProvider Services => _app?.Services
        ?? throw new InvalidOperationException("CreateClientAsync builds the server; call it first.");

    public async Task<McpClient> CreateClientAsync()
    {
        // Full access mode keeps the workspace consolidate/discard E2E flows working under FR-NM-2
        // (docs/work/features-native-memory/native-memory.feature); the settings row is read per call.
        await SeedGlobalAccessModeAsync();
        var app = _app ??= await StartServerAsync(TestContext.Current.CancellationToken);
        var endpoint = new Uri($"{app.Urls.First().TrimEnd('/')}/mcp");
        var httpClient = new HttpClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "e2e-test",
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)),
            true);
        return await McpClient.CreateAsync(transport);
    }

    private async Task<WebApplication> StartServerAsync(CancellationToken cancellationToken)
    {
        var options = new InfrastructureOptions { DataRoot = DataRoot, Scope = _scope };
        var app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, options));
        await app.StartAsync(cancellationToken);
        return app;
    }

    private async Task SeedGlobalAccessModeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = DataRoot, Scope = _scope };
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), TimeProvider.System,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        await store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, AccessModePolicy.Serialize(AccessMode.Full));

        // Ingest is contained by the declared scope, so the E2E server is configured with one
        // just as a real deployment is; the surface tests ingest from temp paths.
        await store.SetSettingAsync(IngestScopeKeys.ScopeGlobal,
            IngestScopeKeys.Serialize([Path.GetTempPath(), DataRoot]));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _app?.StopAsync().GetAwaiter().GetResult();
        _app?.DisposeAsync().GetAwaiter().GetResult();
        _app = null;
        TestData.DeleteTempRoot(DataRoot);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }

        if (!_disposed)
        {
            _disposed = true;
            TestData.DeleteTempRoot(DataRoot);
        }
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot();
}
