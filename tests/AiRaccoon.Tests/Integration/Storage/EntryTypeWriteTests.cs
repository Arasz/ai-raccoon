using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The write paths type what they write: a note is a MEMORY, an ingested document is a FILE,
///     and a note that cites a session transcript is still a MEMORY (the transcript is its
///     locator, not its origin). The type is what the query path filters on, so a row written
///     without one is a row the query cannot place.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EntryTypeWriteTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("entry-type-write");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public EntryTypeWriteTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(),
            TestData.CreateEmbeddingService(), null, null, null, null, new StubCodeChunker(), null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task APlainNote_IsAMemory()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "a plain note"), TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync("acme")).ShouldBe("memory");
    }

    /// <summary>
    ///     A note that cites a session transcript is a MEMORY: the agent wrote it, and the
    ///     transcript is only where it was written down. The cheap signature (a source file, no
    ///     chunks) would file it as a document.
    /// </summary>
    [RetryFact]
    public async Task ANoteCitingATranscript_IsAMemory()
    {
        await _store.WriteAsync(
            new MemoryWriteRequest("acme", "what the session said", SourceFile: "hermes/20260806_215718_fd7f66"),
            TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync("acme")).ShouldBe("memory");
    }

    /// <summary>An ingested document is a FILE: it mirrors a file on disk.</summary>
    [RetryFact]
    public async Task AnIngestedDocument_IsAFile()
    {
        var contentRoot = Path.Combine(_dataRoot, "content");
        Directory.CreateDirectory(contentRoot);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"),
            IngestScopeKeys.Serialize([contentRoot]), TestContext.Current.CancellationToken);
        var path = Path.Combine(contentRoot, "guide.md");
        await File.WriteAllTextAsync(path, "# guide\n\nzephyr ingested document\n", TestContext.Current.CancellationToken);

        await _store.IngestFileAsync("acme", path, null, TestContext.Current.CancellationToken);

        (await ReadEntryTypeAsync("acme")).ShouldBe("file");
    }

    /// <summary>Every row the write paths produce carries a type - none is left unclassified.</summary>
    [RetryFact]
    public async Task NoWrittenRow_IsLeftUnclassified()
    {
        await _store.WriteAsync(new MemoryWriteRequest("acme", "a plain note"), TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var untyped = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM entries WHERE entry_type IS NULL");
        untyped.ShouldBe(0, "a row without a type is a row the query cannot place");
    }

    private async Task<string?> ReadEntryTypeAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT entry_type FROM entries WHERE project_id = @projectId LIMIT 1", new { projectId });
    }
}
