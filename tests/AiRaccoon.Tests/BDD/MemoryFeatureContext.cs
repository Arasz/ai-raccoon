using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.BDD;

/// <summary>Shared state for Reqnroll feature scenarios — one instance per scenario.</summary>
public class MemoryFeatureContext : IDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryFeatureContext()
    {
        DataRoot = CreateTempRoot();
        Factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64", Scope = InstallScope.User }));
        Store = TestData.CreateMemoryStore(Factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(Factory), new StubChunker(), TimeProvider, TestData.CreateEmbeddingService());
    }

    public string DataRoot { get; }

    public SqliteConnectionFactory Factory { get; }

    public FakeTimeProvider TimeProvider { get; } = new(FixedNow);

    public SqliteMemoryStore Store { get; }

    /// <summary>Idempotent so scenario-container disposal and the AfterScenario hook can both run it.</summary>
    public void Dispose()
    {
        TestData.DeleteTempRoot(DataRoot);
    }

    /// <summary>Opens the bank and returns the connection for raw SQL queries.</summary>
    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) => await Factory.OpenBankAsync(cancellationToken);

    private static string CreateTempRoot() => TestData.CreateTempRoot();
}
