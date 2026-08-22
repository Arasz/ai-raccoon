using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
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
    private readonly string _testDir;

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

        if (!withCodeSupport)
        {
            return new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
                NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
                NullWatchStore.Instance, NullEmbedDrainPump.Instance);
        }

        var codeIngestor = new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), TimeProvider.System);
        return new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            NullIgnoreRulesProvider.Instance, new CodeFileTypeMatcher(), codeIngestor,
            NullWatchStore.Instance, NullEmbedDrainPump.Instance);
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

        result.RowsInserted.ShouldBe(1);
        (await CountCodeAsync(file)).ShouldBeGreaterThan(0);
        (await CountAsync("entries")).ShouldBe(0);
    }

    /// <summary>
    ///     S3: an empty/whitespace-only code file legitimately chunks to zero rows FOREVER (unlike
    ///     the B1 stand-in-chunker case, where zero rows is a temporary tooling gap) — it must still
    ///     be fingerprint-eligible, or the watch digest hash-skip never engages and the file
    ///     re-ingests on every poll indefinitely.
    /// </summary>
    [Fact]
    public async Task IngestFileAsync_WhitespaceOnlyCodeFile_IsFingerprintEligible()
    {
        var ingestor = CreateIngestor();
        var file = await WriteAsync("Empty.cs", "   \n\n\t\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.RowsInserted.ShouldBe(0);
        result.FingerprintEligible.ShouldBeTrue(
            "a whitespace-only code file must fingerprint despite zero rows, or the watch digest re-ingests it forever");
    }

    [Fact]
    public async Task IngestFileAsync_WithoutCodeSupportConfigured_CodeFileReturnsZero()
    {
        var ingestor = CreateIngestor(withCodeSupport: false);
        var file = await WriteAsync("Program.cs", "class Program\n{\n}\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.RowsInserted.ShouldBe(0);
        (await CountCodeAsync(file)).ShouldBe(0);
    }

    /// <summary>
    ///     B2 fix (1): a nested target with NO ignore file co-located next to it — only the ROOT
    ///     (here, the ingest-scope allowlist entry, since no watch is registered) carries
    ///     `ai-raccoon.ignore`. The old "parent directory as root" shape would look for the ignore
    ///     file at `src/gen/` (where it does not exist) and miss it entirely; this is the case that
    ///     the original co-located version of this test could never catch.
    /// </summary>
    [Fact]
    public async Task IngestFileAsync_IgnoredCodeFile_NoWatch_FallsBackToScopeAllowlistRoot()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, IgnoreRulesProvider.FileName), "src/gen/*.cs\n",
            TestContext.Current.CancellationToken);
        var ingestor = CreateIngestorWithIgnoreProvider();
        var file = await WriteAsync(Path.Combine("src", "gen", "secret.cs"), "class Secret\n{\n}\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.RowsInserted.ShouldBe(0);
        (await CountCodeAsync(file)).ShouldBe(0);
    }

    /// <summary>B2 fix (1): a registered watch takes priority as the ignore root over the parent
    /// directory (and is checked before falling back to the scope allowlist).</summary>
    [Fact]
    public async Task IngestFileAsync_ExplicitlyIgnoredCodeFile_UnderWatchRoot_ReturnsZeroChunks()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, IgnoreRulesProvider.FileName), "src/gen/*.cs\n",
            TestContext.Current.CancellationToken);
        var watchStore = await RegisterWatchAsync(_testDir);
        var ingestor = CreateIngestorWithIgnoreProvider(watchStore);
        var file = await WriteAsync(Path.Combine("src", "gen", "secret.cs"), "class Secret\n{\n}\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.RowsInserted.ShouldBe(0);
        (await CountCodeAsync(file)).ShouldBe(0);
    }

    /// <summary>B2 fix (2): the ignore check now sits above the memory/code handler branch, so a
    /// memory file (`.md`) under an ignored path is skipped too — not just code files.</summary>
    [Fact]
    public async Task IngestFileAsync_ExplicitlyIgnoredMemoryFile_UnderWatchRoot_ReturnsZeroChunks()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, IgnoreRulesProvider.FileName), "docs/*.md\n",
            TestContext.Current.CancellationToken);
        var watchStore = await RegisterWatchAsync(_testDir);
        var ingestor = CreateIngestorWithIgnoreProvider(watchStore);
        var file = await WriteAsync(Path.Combine("docs", "secret.md"), "# secret\n");

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.RowsInserted.ShouldBe(0);
        (await CountAsync("entries")).ShouldBe(0);
    }

    /// <summary>B2 fix (1): `IgnoreRulesProvider` reads exactly one file per resolved root — a
    /// stray `ai-raccoon.ignore` sitting next to the target (not at the watch root) must never be
    /// discovered or honored.</summary>
    [Fact]
    public async Task IngestFileAsync_StrayNestedIgnoreFile_IsNotHonored()
    {
        var watchStore = await RegisterWatchAsync(_testDir);
        var ingestor = CreateIngestorWithIgnoreProvider(watchStore);
        var file = await WriteAsync(Path.Combine("src", "gen", "a.cs"), "class A\n{\n}\n");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "src", "gen", IgnoreRulesProvider.FileName),
            "a.cs\n", TestContext.Current.CancellationToken);

        var result = await ingestor.IngestFileAsync(_conn, "test_project", file, null,
            TestContext.Current.CancellationToken);

        result.RowsInserted.ShouldBe(1);
        (await CountCodeAsync(file)).ShouldBeGreaterThan(0);
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

    private FileIngestor CreateIngestorWithIgnoreProvider(IWatchStore? watchStore = null)
    {
        var sourceStore = new SqliteMemorySourceStore(
            new SqliteConnectionFactory(
                new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User },
                NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User })));
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var codeIngestor = new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), TimeProvider.System);
        return new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
            new IgnoreRulesProvider(), new CodeFileTypeMatcher(), codeIngestor,
            watchStore ?? NullWatchStore.Instance, NullEmbedDrainPump.Instance);
    }

    private async Task<IWatchStore> RegisterWatchAsync(string path)
    {
        var opts = new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User };
        var watchStore = new WatchStore(new SqliteConnectionFactory(opts, NullKeyProvider.Resolver(opts)));
        await watchStore.AddWatchAsync("test_project", path, 0, 0, TestContext.Current.CancellationToken);
        return watchStore;
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
