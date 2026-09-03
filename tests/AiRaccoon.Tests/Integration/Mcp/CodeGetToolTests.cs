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
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     WP6-T08/T09/T07 — code_get mirrors memory_get: known-hash round-trip, unknown-hash refusal
///     (the same UnknownHashException family, so ToolRefusals' type-based mapping already covers
///     it), and the access gate (Read -- allowed in every mode, exactly like memory_get).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CodeGetToolTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-code-get-tests");
    private SqliteConnectionFactory _factory = null!;
    private CodeTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var codeSearch = new SqliteCodeSearchService(_factory, new FakeCodeEmbedder());
        var settings = new SqliteSettingsStore(_factory);
        var access = new MemoryAccessGuard(new SettingsOnlyStore(settings));
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), new NeverMigratedGate());
        _tools = new CodeTools(codeSearch, gate);
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [RetryFact]
    public async Task CodeGet_KnownHash_ReturnsFullSourceWithPathAndRange()
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Bar.cs", value: "sealed class NarwhalTusk { }",
            lineStart: 5, lineEnd: 9);

        var envelope = await _tools.CodeGet("acme", "hash-1", TestContext.Current.CancellationToken);

        envelope.Data!.Hash.ShouldBe("hash-1");
        envelope.Data!.Value.ShouldBe("sealed class NarwhalTusk { }");
        envelope.Data!.Path.ShouldBe("src/Bar.cs");
        envelope.Data!.LineStart.ShouldBe(5);
        envelope.Data!.LineEnd.ShouldBe(9);
    }

    [RetryFact]
    public async Task CodeGet_UnknownHash_ThrowsUnknownHashException()
    {
        var ex = await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.CodeGet("acme", "does-not-exist", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("does-not-exist");
        ex.Message.ShouldContain("acme");
    }

    [RetryFact]
    public async Task CodeGet_KnownHashInAnotherProject_IsRefused_NeverLeaksAcrossProjects()
    {
        await SeedAsync(id: 1, projectId: "other", path: "src/Bar.cs", value: "class X { }", lineStart: 1, lineEnd: 1);

        await Should.ThrowAsync<UnknownHashException>(() =>
            _tools.CodeGet("acme", "hash-1", TestContext.Current.CancellationToken));
    }

    [RetryTheory]
    [InlineData("ro")]
    [InlineData("rw")]
    [InlineData("full")]
    public async Task CodeGet_SucceedsInEveryAccessMode(string mode)
    {
        await SeedAsync(id: 1, projectId: "acme", path: "src/Bar.cs", value: "class X { }", lineStart: 1, lineEnd: 1);
        var settings = new SqliteSettingsStore(_factory);
        await settings.SetSettingAsync(AccessModePolicy.ProjectSettingKey("acme"), mode, TestContext.Current.CancellationToken);

        var envelope = await _tools.CodeGet("acme", "hash-1", TestContext.Current.CancellationToken);

        envelope.Data!.Hash.ShouldBe("hash-1");
    }

    [RetryFact]
    public async Task CodeGet_BlankProjectId_IsRefused()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.CodeGet("", "hash-1", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("projectId is required (no registered project's scope contains cwd");
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

    /// <summary>MemoryAccessGuard only needs settings reads; every other IMemoryStore member is unused by CodeTools.</summary>
    private sealed class SettingsOnlyStore(SqliteSettingsStore settings) : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            settings.GetSettingAsync(key, cancellationToken);

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            settings.GetSettingsByPrefixAsync(prefix, cancellationToken);
    }
}
