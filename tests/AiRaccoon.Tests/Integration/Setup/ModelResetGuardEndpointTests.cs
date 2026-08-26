using System.Net;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     The server half of the #592 reset guard: the CLI → HTTP → endpoint →
///     <c>SqliteSettingsStore</c> → bank seam, seeded by raw SQL against the live WAL bank
///     (precedent <c>NonDefaultDimensionMigrationTests</c>; the future lease blocks the relay's
///     <c>AcquireModelMigrationLease</c> WHERE, MemorySql.cs:501-503). The refused verb asserts the
///     frozen message verbatim on stderr — the 409 body → client → catch fidelity proof.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ModelResetGuardEndpointTests : IAsyncLifetime
{
    private const string Token = "model-reset-guard-token";

    private static readonly string[] SixKeys =
    [
        EmbeddingSettingsKeys.Provider, EmbeddingSettingsKeys.Model, EmbeddingSettingsKeys.BaseUrl,
        EmbeddingSettingsKeys.Engine, EmbeddingSettingsKeys.ApiKey, EmbeddingSettingsKeys.Dimensions
    ];

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-model-reset-guard");
    private InfrastructureOptions _options = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private ServerSettingsStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _app = McpServerSetup.CreateWebHost(new ServerConfig(0, McpTransport.Http, _options) { McpToken = Token });
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _client.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, Token);
        _store = new ServerSettingsStore(_client, Token);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>RED today: the reset succeeds (exit 0), all six keys vanish, and the outbox row
    /// stays open — the #592 defect. Both verb spellings (ConfigCommands.cs:58,62) share one handler.</summary>
    [RetryTheory]
    [InlineData("settings model reset")]
    [InlineData("settings model embedding reset")]
    public async Task ModelReset_WithAnOpenMigration_IsRefused_Exit25_DeletesNothing(string verb)
    {
        await SeedAsync(openMigration: true);

        var (exit, @out, err) = await RunCliAsync(verb.Split(' '));

        exit.ShouldBe(25, $"reset must be refused while a migration is open (verb: {verb})");
        err.ShouldBe(ModelResetGuardTests.FrozenResetRefusalMessage + Environment.NewLine);
        @out.ShouldNotContain("embedding engine reset to default: no engine");

        foreach (var key in SixKeys)
        {
            (await _store.GetSettingAsync(key, TestContext.Current.CancellationToken))
                .ShouldNotBeNull($"a refused reset must not delete {key}");
        }

        (await OpenMigrationFinishedAtAsync()).ShouldBeNull("the open migration row must survive the refused reset");
    }

    [RetryTheory]
    [InlineData("settings model reset")]
    [InlineData("settings model embedding reset")]
    public async Task ModelReset_NoOpenMigration_StillResets_Exit0_ClearsAllSixKeys(string verb)
    {
        await SeedAsync(openMigration: false);

        var (exit, @out, err) = await RunCliAsync(verb.Split(' '));

        exit.ShouldBe(0);
        @out.ShouldContain("embedding engine reset to default: no engine");
        err.ShouldBeEmpty();

        foreach (var key in SixKeys)
        {
            (await _store.GetSettingAsync(key, TestContext.Current.CancellationToken))
                .ShouldBeNull($"{key} must be gone after a successful reset");
        }

        (await OpenMigrationCountAsync()).ShouldBe(0, "no migration row may exist after a clean reset");
    }

    /// <summary>The guard is Provider-only: `model code reset` and the sync/extract deleters touch
    /// other keys and must keep working while a migration is open.</summary>
    [RetryFact]
    public async Task Delete_NonProviderKey_WhileMigrationOpen_StillSucceeds()
    {
        await SeedAsync(openMigration: true);

        var response = await _client.DeleteAsync("/settings?key=" + EmbeddingSettingsKeys.Model,
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _store.GetSettingAsync(EmbeddingSettingsKeys.Model, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    private async Task<(int Exit, string Out, string Err)> RunCliAsync(string[] verb)
    {
        // The production composition: IMemoryStore forwards settings to LazyServerSettingsStore
        // over the same authed HTTP client (AppRunner.cs:209-236); the fake stands in for that
        // indirection with only the member ModelResetAsync calls given a real body (R2 F6/F14).
        var store = new DelegatingMemoryStore(new LazyServerSettingsStore(
            _ => Task.FromResult<ISettingsStore>(new ServerSettingsStore(_client, Token))));
        var commands = TestData.CreateConfigCommands(store, settings: new SettingsCommands());
        return await CliRun.RunAsync(verb, commands);
    }

    /// <summary>Seeds the six embedding rows and, optionally, an open migration row by raw SQL on a
    /// second connection to the served bank. Idempotent (DELETE + INSERT) for the retry surface;
    /// the future lease blocks the real maintenance relay from draining the row mid-test.</summary>
    private async Task SeedAsync(bool openMigration)
    {
        // Force the server to create the bank before a second connection writes to it.
        await _store.GetSettingAsync(EmbeddingSettingsKeys.Provider, TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection($"Data Source={SqliteConnectionFactory.BankPathFor(_options)}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA busy_timeout = 5000",
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM settings
            WHERE key IN ('embedding.provider','embedding.model','embedding.baseUrl','embedding.engine','embedding.apiKey','embedding.dimensions');
            DELETE FROM model_migration WHERE id = 1;
            """, cancellationToken: TestContext.Current.CancellationToken));

        if (openMigration)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO settings(key, value) VALUES
                    ('embedding.provider','openai'),
                    ('embedding.model','text-embedding-3-small'),
                    ('embedding.baseUrl','https://api.openai.com/v1'),
                    ('embedding.engine','openai'),
                    ('embedding.apiKey','sk-test'),
                    ('embedding.dimensions','1536');
                INSERT INTO model_migration(id, provider, model, base_url, engine, started_at, finished_at, lease_owner, lease_expires_at)
                VALUES (1, 'openai', 'text-embedding-3-small', NULL, 'test-engine', 0, NULL, 'test-holder', @leaseExpiresAt);
                """,
                new { leaseExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() },
                cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    private async Task<long?> OpenMigrationFinishedAtAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={SqliteConnectionFactory.BankPathFor(_options)}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA busy_timeout = 5000",
            cancellationToken: TestContext.Current.CancellationToken));
        return await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT finished_at FROM model_migration WHERE id = 1", cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> OpenMigrationCountAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={SqliteConnectionFactory.BankPathFor(_options)}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA busy_timeout = 5000",
            cancellationToken: TestContext.Current.CancellationToken));
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM model_migration", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Same shape as <c>PerformanceCommandsRoundTripTests.DelegatingMemoryStore</c>: forwards
    /// IMemoryStore's settings surface to a given ISettingsStore — here the real lazy HTTP client.</summary>
    private sealed class DelegatingMemoryStore(ISettingsStore inner) : FakeMemoryStore
    {
        public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            inner.DeleteSettingAsync(key, cancellationToken);
    }
}
