using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Tests;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using ModelContextProtocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     WP6-T08/T09/T07 — get_entry reads the code corpus: known-hash round-trip, unknown-hash refusal
///     (the same UnknownHashException family, so ToolRefusals' type-based mapping already covers
///     it), and the access gate (Read -- allowed in every mode, exactly like get_entry).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class GetEntryCodeToolTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-get-tests");
    private SqliteConnectionFactory _factory = null!;
    private EntryTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var codeSearch = new SqliteCodeSearchService(_factory, new FakeCodeEmbedder());
        var settings = new SqliteSettingsStore(_factory);
        var access = new MemoryAccessGuard(new SettingsOnlyStore(settings));
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), TestData.ScopeCheck);
        var store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _tools = new EntryTools(store, codeSearch, gate);
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [RetryFact]
    public async Task GetEntry_KnownHash_ReturnsFullSourceWithPathAndRange()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Bar.cs", value: "sealed class NarwhalTusk { }",
            lineStart: 5, lineEnd: 9);

        var envelope = await _tools.GetEntry("acme", "hash-1", "code", TestContext.Current.CancellationToken);

        envelope.Data!.Hash.ShouldBe("hash-1");
        envelope.Data!.Value.ShouldBe("sealed class NarwhalTusk { }");
        envelope.Data!.Path.ShouldBe("src/Bar.cs");
        envelope.Data!.LineStart.ShouldBe(5);
        envelope.Data!.LineEnd.ShouldBe(9);
    }

    [RetryFact]
    public async Task GetEntry_UnknownHash_ThrowsUnknownHashException()
    {
        var ex = await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.GetEntry("acme", "does-not-exist", "code", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("does-not-exist");
        ex.Message.ShouldContain("acme");
    }

    [RetryFact]
    public async Task GetEntry_KnownHashInAnotherProject_IsRefused_NeverLeaksAcrossProjects()
    {
        await SeedAsync(id: 1, projectId: "other", path: "src/Bar.cs", value: "class X { }", lineStart: 1, lineEnd: 1);

        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.GetEntry("acme", "hash-1", "code", TestContext.Current.CancellationToken));
    }

    [RetryTheory]
    [InlineData("ro")]
    [InlineData("rw")]
    [InlineData("full")]
    public async Task GetEntry_SucceedsInEveryAccessMode(string mode)
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Bar.cs", value: "class X { }", lineStart: 1, lineEnd: 1);
        var settings = new SqliteSettingsStore(_factory);
        await settings.SetSettingAsync(AccessModePolicy.ProjectSettingKey("acme"), mode, TestContext.Current.CancellationToken);

        var envelope = await _tools.GetEntry("acme", "hash-1", "code", TestContext.Current.CancellationToken);

        envelope.Data!.Hash.ShouldBe("hash-1");
    }

    [RetryFact]
    public async Task GetEntry_BlankProjectId_IsRefused()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.GetEntry("", "hash-1", "code", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("projectId is required (no registered project's scope contains cwd");
    }

    [RetryFact]
    public async Task GetEntry_OverlappingHash_SelectsRequestedKindBeforeChoosingRow()
    {
        await SeedAsync(1, "acme", "Code.cs", "code value", 1, 2);
        await SeedMemoryAsync("hash-1", "note.md", "memory value", "memory", "project", "acme");
        await SeedMemoryAsync("hash-1", "file.md", "file value", "file", "shared", null);

        var memory = (await _tools.GetEntry("acme", "hash-1", "memory", TestContext.Current.CancellationToken)).Data!;
        var file = (await _tools.GetEntry("acme", "hash-1", "file", TestContext.Current.CancellationToken)).Data!;
        var code = (await _tools.GetEntry("acme", "hash-1", "code", TestContext.Current.CancellationToken)).Data!;

        memory.Value.ShouldBe("memory value");
        memory.EntryType.ShouldBe("memory");
        memory.EntryKind.ShouldBe("memory");
        file.Value.ShouldBe("file value");
        file.EntryType.ShouldBe("memory");
        file.EntryKind.ShouldBe("file");
        code.Value.ShouldBe("code value");
        code.EntryType.ShouldBe("code");
        code.EntryKind.ShouldBe("code");
    }

    [RetryTheory]
    [InlineData("none")]
    [InlineData("future")]
    public async Task GetEntry_UnclassifiedOrUnknownStoredKind_DoesNotGuessFromShape(string storedKind)
    {
        await SeedMemoryAsync("unclassified", "note.md", "agent note", storedKind, "project", "acme");
        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.GetEntry("acme", "unclassified", "memory", TestContext.Current.CancellationToken));
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<string>("SELECT entry_type FROM entries WHERE hash='unclassified'"))
            .ShouldBe(storedKind);
    }

    [RetryFact]
    public async Task GetEntry_WrongTypeOrProject_NeverFallsBackToAnotherCorpus()
    {
        await SeedMemoryAsync("note", "note.md", "private note", "memory", "project", "other");
        await SeedMemoryAsync("file", "file.md", "document", "file", "project", "acme");
        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.GetEntry("acme", "note", "memory", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.GetEntry("acme", "file", "memory", TestContext.Current.CancellationToken));
        var file = await _tools.GetEntry("acme", "file", "file", TestContext.Current.CancellationToken);
        file.Data!.Value.ShouldBe("document");
    }

    [RetryTheory]
    [InlineData("")]
    [InlineData("memory,file")]
    [InlineData("none")]
    [InlineData("8")]
    public async Task GetEntry_InvalidSelection_IsInvalidParams(string entryType)
    {
        var error = await Should.ThrowAsync<McpException>(() =>
            _tools.GetEntry("acme", "hash-1", entryType, TestContext.Current.CancellationToken));
        error.Message.ShouldStartWith("invalid-params:");
    }

    private async Task SeedMemoryAsync(string hash, string path, string value, string kind, string scope, string? projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            PRAGMA ignore_check_constraints=ON;
            INSERT INTO entries(hash,path,value,entry_type,scope,project_id,created_at,updated_at)
            VALUES(@hash,@path,@value,@kind,@scope,@projectId,1,1);
            """, new { hash, path, value, kind, scope, projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedAsync(long id, string projectId, string path, string value, int lineStart, int lineEnd)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @path, @lineStart, @lineEnd, @projectId, 1, 1)
            """,
            new { id, hash = $"hash-{id}", path, value, lineStart, lineEnd, projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The merged get_entry reads the memory corpus before the code corpus, so the code-side
    ///     test needs a memory store that owns nothing — every hash it is asked for is the code
    ///     corpus's.
    /// </summary>
    private sealed class EmptyStore : FakeMemoryStore
    {
        public override Task<MemoryEntry?> GetAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MemoryEntry?>(null);
    }

    /// <summary>MemoryAccessGuard only needs settings reads; every other IMemoryStore member is unused by the gate.</summary>
    private sealed class SettingsOnlyStore(SqliteSettingsStore settings) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            settings.GetSettingAsync(key, cancellationToken);

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            settings.GetSettingsByPrefixAsync(prefix, cancellationToken);
    }
}
