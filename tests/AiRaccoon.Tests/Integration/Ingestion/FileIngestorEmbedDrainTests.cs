using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Ingestion;

/// <summary>
///     A directory ingest embeds nothing itself — <see cref="FileIngestor" /> does not hold an
///     <c>IEntryEmbedder</c> at all — and signals the embed topic once per corpus the walk wrote
///     rows for (docs/work/2026-08-22-post-delta-3-plan.md).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FileIngestorEmbedDrainTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _testDir;

    public FileIngestorEmbedDrainTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "airaccoon_embed_drain_dir_" + Guid.NewGuid().ToString("N"));
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

    private FileIngestor NewIngestor(IEventPump<EmbedDrainRequest> pump, bool withCodeSupport = false)
    {
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(TestData.RealMarkdownChunker())]);
        var sourceStore = new SqliteMemorySourceStore(new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User },
            NullKeyProvider.Resolver(new InfrastructureOptions { DataRoot = _testDir, Rid = "osx-arm64", Scope = InstallScope.User })));
        return withCodeSupport
            ? new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
                NullIgnoreRulesProvider.Instance, new CodeFileTypeMatcher(),
                new CodeIngestor(new CodeFileTypeMatcher(), new StubCodeChunker(), TimeProvider.System),
                NullWatchStore.Instance, pump)
            : new FileIngestor(matcher, sourceStore, TimeProvider.System, TestData.CreateEmbeddingService(),
                NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance, NullCodeIngestor.Instance,
                NullWatchStore.Instance, pump);
    }

    [Fact]
    public async Task IngestDirectory_EmbedsNoChunkInline_AndSignalsOnce()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "a.md"), "# A\ncontent a", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "b.md"), "# B\ncontent b", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "c.md"), "# C\ncontent c", TestContext.Current.CancellationToken);
        var pump = TestData.NewEmbedDrainPump();
        var ingestor = NewIngestor(pump);

        var result = await ingestor.IngestDirectoryAsync(_conn, "acme", _testDir, null, TestContext.Current.CancellationToken);

        result.Indexed.ShouldBe(3);
        pump.EnqueuedCount.ShouldBe(1, "one signal for the whole walk, not one per file");
        var queued = pump.DrainUpTo(1).ShouldHaveSingleItem();
        queued.Corpus.ShouldBe(EmbedCorpus.Memory);
    }

    [Fact]
    public async Task IngestDirectory_NoRowsIndexed_NeverSignals()
    {
        var pump = TestData.NewEmbedDrainPump();
        var ingestor = NewIngestor(pump);

        var result = await ingestor.IngestDirectoryAsync(_conn, "acme", _testDir, null, TestContext.Current.CancellationToken);

        result.Indexed.ShouldBe(0);
        pump.EnqueuedCount.ShouldBe(0, "nothing was left pending — no reason to wake the drain consumer");
    }

    /// <summary>A code-only walk must wake the code drain, not the memory one — the walk never wrote a memory row.</summary>
    [Fact]
    public async Task IngestDirectory_CodeOnly_SignalsCodeCorpusOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "a.cs"), "class A\n{\n}\n", TestContext.Current.CancellationToken);
        var pump = TestData.NewEmbedDrainPump();
        var ingestor = NewIngestor(pump, withCodeSupport: true);

        await ingestor.IngestDirectoryAsync(_conn, "acme", _testDir, null, TestContext.Current.CancellationToken);

        var queued = pump.DrainUpTo(10);
        queued.ShouldHaveSingleItem().Corpus.ShouldBe(EmbedCorpus.Code);
    }

    /// <summary>A walk that touches both corpora signals both — one drain per corpus it actually wrote rows for.</summary>
    [Fact]
    public async Task IngestDirectory_MixedRepo_SignalsBothCorpora()
    {
        await File.WriteAllTextAsync(Path.Combine(_testDir, "readme.md"), "# Readme\nbody", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "a.cs"), "class A\n{\n}\n", TestContext.Current.CancellationToken);
        var pump = TestData.NewEmbedDrainPump();
        var ingestor = NewIngestor(pump, withCodeSupport: true);

        await ingestor.IngestDirectoryAsync(_conn, "acme", _testDir, null, TestContext.Current.CancellationToken);

        var queued = pump.DrainUpTo(10);
        queued.Count.ShouldBe(2, "both corpora were written to and must both wake their drain");
        queued.ShouldContain(r => r.Corpus == EmbedCorpus.Memory);
        queued.ShouldContain(r => r.Corpus == EmbedCorpus.Code);
    }
}
