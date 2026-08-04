using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.Reqnroll;

/// <summary>Shared state for Reqnroll feature scenarios — one instance per scenario.</summary>
public sealed class MemoryFeatureContext : IDisposable
{
    public string DataRoot { get; }

    public SqliteConnectionFactory Factory { get; }

    public FakeTimeProvider TimeProvider { get; } = new(FixedNow);

    public SqliteMemoryStore Store { get; }

    public static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public MemoryFeatureContext()
    {
        DataRoot = CreateTempRoot();
        Factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = DataRoot, Rid = "osx-arm64" },
            new NullKeyProvider());
        Store = new SqliteMemoryStore(Factory, TimeProvider, new StubChunker(),
            new Infrastructure.Embedding.EmbeddingService());
    }

    public void Dispose() => Directory.Delete(DataRoot, true);

    /// <summary>Opens the bank and returns the connection for raw SQL queries.</summary>
    public async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) =>
        await Factory.OpenBankAsync(cancellationToken);

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "airaccoon-reqnroll", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
