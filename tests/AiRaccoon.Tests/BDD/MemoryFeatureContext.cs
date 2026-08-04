using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.BDD;

/// <summary>Shared state for Reqnroll feature scenarios — one instance per scenario.</summary>
public class MemoryFeatureContext : IDisposable
{
    public static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryFeatureContext()
    {
        DataRoot = CreateTempRoot();
        Factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        Store = new SqliteMemoryStore(Factory, TimeProvider, new StubChunker(),
            new EmbeddingService());
    }

    public string DataRoot { get; }

    public SqliteConnectionFactory Factory { get; }

    public FakeTimeProvider TimeProvider { get; } = new(FixedNow);

    public SqliteMemoryStore Store { get; }

    /// <summary>Idempotent so scenario-container disposal and the AfterScenario hook can both run it.</summary>
    public void Dispose()
    {
        if (Directory.Exists(DataRoot))
        {
            Directory.Delete(DataRoot, true);
        }
    }

    /// <summary>Opens the bank and returns the connection for raw SQL queries.</summary>
    public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) => await Factory.OpenBankAsync(cancellationToken);

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("ai-raccoon-tests");

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
