using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>A file no corpus can take reports why and is never fingerprint-eligible; intentional exclusions and blank files stay eligible.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FileIngestorSkipReasonTests : IDisposable
{
    private const string Project = "acme";
    private readonly SqliteConnection _conn;
    private readonly FileIngestor _ingestor;
    private readonly string _dir;
    private readonly SqliteConnectionFactory _factory;

    public FileIngestorSkipReasonTests()
    {
        _dir = TestData.CreateTempRoot("airaccoon-skip-reason");
        var opts = new InfrastructureOptions { DataRoot = _dir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _factory = factory;
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();
        var matcher = new FileTypeMatcher([
            new MarkdownFileTypeHandler(TestData.RealMarkdownChunker()),
            new NotebookFileTypeHandler(new NotebookChunker(TestData.RealMarkdownChunker()))
        ]);
        _ingestor = new FileIngestor(matcher, new SqliteMemorySourceStore(factory), TimeProvider.System,
            TestData.CreateEmbeddingService(), NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance,
            NullCodeIngestor.Instance, NullWatchStore.Instance, NullEmbedDrainPump.Instance, new IndexableFileWalk(), TestData.SharedScope, new EntryTypeClassifier());

        using var scope = _conn.CreateCommand();
        scope.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scope.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scope.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_dir]));
        scope.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_dir);
    }

    [RetryTheory]
    [InlineData("tool.xyz", "plain text nobody routes", SkipReason.UnsupportedType)]
    [InlineData("image.xyz", "PNG\0\0\u0001binary", SkipReason.Binary)]
    [InlineData("notes.md", "# Title\0\0binary body", SkipReason.Binary)]
    [InlineData("broken.ipynb", "{ this is not json", SkipReason.Unparseable)]
    public async Task IngestFileAsync_FileNoCorpusCanTake_ReportsTheReason_AndIsNotFingerprintEligible(string name,
        string content, SkipReason expected)
    {
        var path = await WriteAsync(name, content);

        var result = await _ingestor.IngestFileAsync(_conn, Project, path, null, TestContext.Current.CancellationToken);

        result.Skipped.ShouldBe(expected);
        result.FingerprintEligible.ShouldBeFalse();
        result.RowsInserted.ShouldBe(0);
    }

    [RetryTheory]
    [InlineData("empty.md", "")]
    [InlineData("blank.md", "   \n\t")]
    [InlineData("blank.ipynb", "  \n")]
    [InlineData("ai-raccoon.ignore", "*.tmp\n")]
    [InlineData(".hidden.xyz", "secret")]
    [InlineData("real.md", "# Heading\n\nbody text")]
    public async Task IngestFileAsync_BlankExcludedOrIndexedFile_IsNotSkipped_AndStaysFingerprintEligible(string name,
        string content)
    {
        var path = await WriteAsync(name, content);

        var result = await _ingestor.IngestFileAsync(_conn, Project, path, null, TestContext.Current.CancellationToken);

        result.Skipped.ShouldBeNull();
        result.FingerprintEligible.ShouldBeTrue();
    }

    [RetryFact]
    public async Task IngestFileAsync_BinaryFileWithACodeExtension_IsSkippedAsBinary_AndNotFingerprintEligible()
    {
        var path = await WriteAsync("script.py", "#!\0\0binary body");
        var codeIngestor = new BinaryAwareCodeIngestor();
        var ingestor = new FileIngestor(new FileTypeMatcher([]), new SqliteMemorySourceStore(_factory), TimeProvider.System,
            TestData.CreateEmbeddingService(), NullIgnoreRulesProvider.Instance, new CodeFileTypeMatcher(),
            codeIngestor, NullWatchStore.Instance, NullEmbedDrainPump.Instance, new IndexableFileWalk(), TestData.SharedScope, new EntryTypeClassifier());

        var result = await ingestor.IngestFileAsync(_conn, Project, path, null, TestContext.Current.CancellationToken);

        result.Skipped.ShouldBe(SkipReason.Binary);
        result.FingerprintEligible.ShouldBeFalse();
        result.CodeChunkHashes.ShouldNotBeNull("null would tell the prune to keep the file's old code rows").ShouldBeEmpty();
    }

    /// <summary>Answers binary content the way <see cref="CodeIngestor" /> does: zero rows, nothing to chunk.</summary>
    private sealed class BinaryAwareCodeIngestor : ICodeIngestor
    {
        public Task<CodeIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
            CancellationToken cancellationToken, IReadOnlyList<string>? scope = null) =>
            Task.FromResult(new CodeIngestResult(0, true, []));
    }

    private async Task<string> WriteAsync(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }
}
