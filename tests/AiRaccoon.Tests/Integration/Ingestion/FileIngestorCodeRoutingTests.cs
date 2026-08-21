using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     WP3-T10/OQ4 (docs/work/2026-08-21-code-search-implementation-plan.md §12.4): one
///     `memory_ingest_directory`/`memory_ingest_file` pass routes each file to its own corpus by
///     extension — `.md` to `entries`, code extensions to `code_entries`, unsupported to neither.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class FileIngestorCodeRoutingTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly string _testDir;
    private readonly TimeProvider _timeProvider = new FakeTimeProvider();

    public FileIngestorCodeRoutingTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_code_routing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);

        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts));
        _conn = factory.OpenBankAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var scopeCmd = _conn.CreateCommand();
        scopeCmd.CommandText = "INSERT INTO settings (key, value) VALUES (@key, @scope);";
        scopeCmd.Parameters.AddWithValue("@key", IngestScopeKeys.ScopeGlobal);
        scopeCmd.Parameters.AddWithValue("@scope", IngestScopeKeys.Serialize([_testDir]));
        scopeCmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _conn.Dispose();
        TestData.DeleteTempRoot(_testDir);
    }

    private FileIngestor CreateIngestor(bool withCodeSupport = true)
    {
        var sourceStore = new SqliteMemorySourceStore(
            new SqliteConnectionFactory(
                new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User },
                NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User })));
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var embedder = new EntryEmbedder(TestData.CreateEmbeddingService(), _modelMigrationLease, _timeProvider);

        if (!withCodeSupport)
        {
            return new FileIngestor(matcher, embedder, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService());
        }

        var codeIngestor = new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), TimeProvider.System);
        return new FileIngestor(matcher, embedder, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            codeFileTypeMatcher: new CodeFileTypeMatcher(), codeIngestor: codeIngestor);
    }

    [Fact]
    public async Task IngestDirectoryAsync_MixedRepo_RoutesEachFileByExtension()
    {
        var ingestor = CreateIngestor();
        await WriteAsync("README.md", "# hello\n");
        await WriteAsync(Path.Combine("src", "A.cs"), "class A\n{\n}\n");
        await WriteAsync(Path.Combine("src", "B.py"), "def f():\n    pass\n");
        await WriteAsync("logo.png", "not really an image");

        await ingestor.IngestDirectoryAsync(_conn, "test_project", _testDir, null,
            TestContext.Current.CancellationToken);

        (await CountAsync("entries")).ShouldBe(1);
        (await CountCodeAsync(Path.Combine(_testDir, "src", "A.cs"))).ShouldBeGreaterThan(0);
        (await CountCodeAsync(Path.Combine(_testDir, "src", "B.py"))).ShouldBeGreaterThan(0);
        (await CountCodeAsync(Path.Combine(_testDir, "logo.png"))).ShouldBe(0);
    }

    [Fact]
    public async Task IngestFileAsync_ExplicitCodeFile_RoutesToCodeCorpus()
    {
        var ingestor = CreateIngestor();
        var file = await WriteAsync("Program.cs", "class Program\n{\n}\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.ShouldBe(1);
        (await CountCodeAsync(file)).ShouldBeGreaterThan(0);
        (await CountAsync("entries")).ShouldBe(0);
    }

    [Fact]
    public async Task IngestFileAsync_WithoutCodeSupportConfigured_CodeFileReturnsZero()
    {
        var ingestor = CreateIngestor(withCodeSupport: false);
        var file = await WriteAsync("Program.cs", "class Program\n{\n}\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        (await CountCodeAsync(file)).ShouldBe(0);
    }

    [Fact]
    public async Task IngestFileAsync_ExplicitlyIgnoredCodeFile_ReturnsZeroChunks()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, IgnoreRulesProvider.FileName), "secret.cs\n",
            TestContext.Current.CancellationToken);
        var ingestor = CreateIngestorWithIgnoreProvider();
        var file = await WriteAsync("secret.cs", "class Secret\n{\n}\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.ShouldBe(0);
        (await CountCodeAsync(file)).ShouldBe(0);
    }

    [Fact]
    public async Task IngestDirectoryAsync_CodeWalk_SkipsHiddenAndDenySet()
    {
        var ingestor = CreateIngestor();
        await WriteAsync("Program.cs", "class Program\n{\n}\n");
        await WriteAsync(".hidden.cs", "class Hidden\n{\n}\n");
        await WriteAsync(Path.Combine(".git", "hooks.cs"), "class Hooks\n{\n}\n");
        await WriteAsync(Path.Combine("obj", "Debug", "x.cs"), "class Generated\n{\n}\n");
        await WriteAsync(Path.Combine("node_modules", "pkg", "index.js"), "function f() {}\n");

        await ingestor.IngestDirectoryAsync(_conn, "test_project", _testDir, null,
            TestContext.Current.CancellationToken);

        (await CountCodeAsync(Path.Combine(_testDir, "Program.cs"))).ShouldBeGreaterThan(0);
        (await TotalCodeCountAsync()).ShouldBe(await CountCodeAsync(Path.Combine(_testDir, "Program.cs")));
    }

    private FileIngestor CreateIngestorWithIgnoreProvider()
    {
        var sourceStore = new SqliteMemorySourceStore(
            new SqliteConnectionFactory(
                new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User },
                NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User })));
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var embedder = new EntryEmbedder(TestData.CreateEmbeddingService(), _modelMigrationLease, _timeProvider);
        var codeIngestor = new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), TimeProvider.System);
        return new FileIngestor(matcher, embedder, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            new IgnoreRulesProvider(), new CodeFileTypeMatcher(), codeIngestor);
    }

    private async Task<string> WriteAsync(string relative, string content)
    {
        var path = Path.Combine(_testDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
        return path;
    }

    private async Task<int> CountAsync(string table) =>
        await _conn.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table}");

    private async Task<int> CountCodeAsync(string path) =>
        await _conn.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) });

    private async Task<int> TotalCodeCountAsync() => await CountAsync("code_entries");
}
