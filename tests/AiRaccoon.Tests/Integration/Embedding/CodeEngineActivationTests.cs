using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using Dapper;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     The code corpus's own activation transaction (§3.3 D-E9): ActivateCodeEngineAsync commits
///     embedding.codeModel/codeEngine and invalidates every embedded code row to 'pending' in ONE
///     transaction — no outbox, no relay. The vec_code_pending trigger (proven in
///     SqliteCodeStoreTests.CodeRow_RoundTrips_ThroughFtsAndVecCodeViaEmbedStateTriggers) is what
///     empties vec_code at commit; this pins that the invalidation actually reaches the trigger
///     through this call, with no drain/reconcile phase in between (unlike the memory bank).
///     <para>
///         B1: the manifest gate lives on the STORE, not just the CLI pre-flight (SettingsCommands.
///         ModelSetCodeLocalAsync) — the HTTP settings endpoint calls this method directly with no
///         CLI in the path, so a refusal test that only drives the CLI never proves the server itself
///         is protected. Every refusal here must leave NOTHING written: no settings rows, no
///         invalidation.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeEngineActivationTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-code-engine-activation");
    private SqliteConnectionFactory _factory = null!;
    private SqliteCodeEngineStore _store = null!;

    public ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteCodeEngineStore(_factory, TestData.CreateEmbeddingService(), TestData.CreateManifestLoader());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_WritesCodeModelAndCodeEngineSettingsRows()
    {
        var dir = Path.Combine(_dataRoot, "code-model");
        TestData.SeedCodeManifestDirectory(dir);

        var config = await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        config.Provider.ShouldBe("local");
        config.Model.ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBe(Path.GetFullPath(dir));
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBe(config.Engine);
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_MarksEmbeddedCodeRowsPending_AndEmptiesVecCodeInTheSameCommit()
    {
        await SeedAnEmbeddedCodeRowAsync();
        (await CountVecCodeRowsAsync()).ShouldBe(1, "the seed must land an embedded row before activation");

        var dir = Path.Combine(_dataRoot, "code-model");
        TestData.SeedCodeManifestDirectory(dir);
        await _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken);

        (await ReadCodeEmbedStateAsync(1)).ShouldBe("pending",
            "activation must invalidate every previously-embedded code row");
        (await CountVecCodeRowsAsync()).ShouldBe(0,
            "the vec_code_pending trigger must empty vec_code in the SAME commit as the settings write " +
            "— no stale-vector window, no separate reconcile phase for code");
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_NonexistentDirectory_RefusesAndWritesNothing()
    {
        var dir = Path.Combine(_dataRoot, "does-not-exist");

        await Should.ThrowAsync<InvalidOperationException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull(
            "a refused activation must never commit the code-model setting row");
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeEngine)).ShouldBeNull(
            "a refused activation must never commit the code-engine setting row");
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_Non768Manifest_RefusesAndWritesNothing()
    {
        var dir = Path.Combine(_dataRoot, "code-model-non768");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        File.Copy(TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1-non768.json"),
            Path.Combine(dir, EmbeddingManifest.FileName));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("1024");
        ex.Message.ShouldContain(CodeCorpusSchema.EmbeddingDimensions.ToString());
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull();
    }

    [Fact]
    public async Task ActivateCodeEngineAsync_768DimButWrongContextWindow_RefusesOnChunkBudget_AndWritesNothing()
    {
        // 768 dims (passes the S4-adjacent dims gate) but a 256-token context resolves to a
        // 254-token chunk budget (min(510, 256-2)), not the 126 CodeChunker is hard-pinned to
        // (S4 ruling, orchestrator-decided — not re-litigated here, only guarded against drift).
        var dir = Path.Combine(_dataRoot, "code-model-wrong-ctx");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = File.ReadAllText(
                TestData.RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"))
            .Replace("\"contextWindowTokens\": 128", "\"contextWindowTokens\": 256");
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName), manifest);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _store.ActivateCodeEngineAsync(dir, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("254");
        ex.Message.ShouldContain(CodeChunker.DefaultBudget.ToString());
        (await ReadSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull();
    }

    private async Task SeedAnEmbeddedCodeRowAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (1, 'hash-1', 'src/foo.cs', 'seed row', 'src/foo.cs', 1, 1, 'acme', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));
        var vector = EmbeddingBlob.ToBytes(Enumerable.Repeat(0.5f, 768).ToArray());
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = 1",
            new { embedding = vector }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string?> ReadSettingAsync(string key)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT value FROM settings WHERE key = @key", new { key },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string> ReadCodeEmbedStateAsync(long id)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM code_entries WHERE id = @id", new { id },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<int> CountVecCodeRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM vec_code", cancellationToken: TestContext.Current.CancellationToken));
    }
}
