using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Real-SQLite proof that LikePattern.Escape earns its place: DeleteSourcePathAsync's subtree
///     cascade runs `path LIKE @pathPrefix ESCAPE '\'`, so an unescaped '_' or '%' in the deleted
///     path matches sibling directories and deletes their rows.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemoryStorePathCascadeEscapeTests : IDisposable
{
    private const string ProjectId = "acme";

    /// <summary>Holds both metacharacters, so one delete exercises the '_' and '%' axes together.</summary>
    private const string Deleted = "/data/a_b%c";

    /// <summary>Differs from <see cref="Deleted" /> only where the metacharacters sit.</summary>
    private const string Decoy = "/data/aXbYYc";

    private const string Unrelated = "/data/other";

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-path-cascade-escape");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public MemoryStorePathCascadeEscapeTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new SingleChunkChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)), TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task DeleteSourcePathAsync_WithWildcardsInThePath_DeletesOnlyTheLiteralSubtree()
    {
        await AddAsync(Deleted, "the directory row itself");
        await AddAsync($"{Deleted}/file.md", "inside the deleted subtree");
        await AddAsync($"{Decoy}/file.md", "a sibling an unescaped LIKE pattern would swallow");
        await AddAsync($"{Unrelated}/file.md", "plainly out of range");

        var deleted = await _store.DeleteSourcePathAsync(ProjectId, Deleted, TestContext.Current.CancellationToken);

        deleted.ShouldBe(2, "only the path itself and its one child are literal matches");
        var remaining = await RemainingPathsAsync();
        remaining.ShouldBe([$"{Decoy}/file.md", $"{Unrelated}/file.md"]);
    }

    private async Task AddAsync(string path, string content) =>
        await _store.AddContentAsync(ProjectId, path, content, null,
            cancellationToken: TestContext.Current.CancellationToken);

    private async Task<List<string>> RemainingPathsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT path FROM entries WHERE project_id = @ProjectId ORDER BY path",
            new { ProjectId }, cancellationToken: TestContext.Current.CancellationToken));
        return [.. rows];
    }
}
