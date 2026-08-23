using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0089 decisions 1/2/3/5: the registry records which project ids exist, storing the
///     canonical lowercase D-form; <see cref="IProjectRegistry.HasRowsAsync" /> answers from
///     <see cref="ProjectRows.Of" /> (per-project membership), never <see cref="ProjectRows.Scope" />
///     (any populated bank).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SqliteProjectRegistryTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 0, 0, 0, TimeSpan.Zero);
    private const string GuidV7 = "01911f9e-712c-7a3e-9c0a-8f2b1c4d5e6f";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-project-registry");
    private readonly SqliteConnectionFactory _factory;
    private readonly IProjectRegistry _registry;

    public SqliteProjectRegistryTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _registry = new SqliteMemoryStore(_factory, Substitute.For<IMemorySourceStore>(), Substitute.For<IFileIngestor>(),
            Substitute.For<IEntryEmbedder>(), new FakeTimeProvider(FixedNow),
            NullLogger<SqliteMemoryStore>.Instance, new NoiseFilteringService([]),
            Substitute.For<ISettingsStore>(), Substitute.For<IEventPump<EmbedDrainRequest>>(),
            NoOpMeasurementRecorder.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task RegisterAsync_MakesIsRegisteredTrue()
    {
        await _registry.RegisterAsync(GuidV7, "acme", TestContext.Current.CancellationToken);

        (await _registry.IsRegisteredAsync(GuidV7, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>Pins "from the injected TimeProvider, never DateTimeOffset.UtcNow" — a wall-clock read would not match FixedNow.</summary>
    [Fact]
    public async Task RegisterAsync_StampsCreatedAtFromTheInjectedTimeProvider()
    {
        await _registry.RegisterAsync(GuidV7, "acme", TestContext.Current.CancellationToken);

        (await ReadCreatedAtAsync(GuidV7)).ShouldBe(FixedNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task RegisterAsync_IsIdempotentForTheSameId()
    {
        await _registry.RegisterAsync(GuidV7, "acme", TestContext.Current.CancellationToken);
        await _registry.RegisterAsync(GuidV7, "renamed", TestContext.Current.CancellationToken);

        (await CountProjectRowsAsync(GuidV7)).ShouldBe(1L, "a second registration of the same id must not insert a second row");
        (await ReadProjectNameAsync(GuidV7)).ShouldBe("acme",
            "ON CONFLICT DO NOTHING is first-write-wins: the second registration's name is silently discarded");
    }

    [Fact]
    public async Task RegisterAsync_StoresTheCanonicalLowercaseDForm()
    {
        var braced = "{" + GuidV7.ToUpperInvariant() + "}";

        await _registry.RegisterAsync(braced, null, TestContext.Current.CancellationToken);

        (await CountProjectRowsAsync(GuidV7)).ShouldBe(1L, "the stored id must be the canonical lowercase D-form, not the raw input");
    }

    [Fact]
    public async Task HasRowsAsync_IsFalseForARegisteredProjectWithNoEntries()
    {
        await _registry.RegisterAsync(GuidV7, "acme", TestContext.Current.CancellationToken);

        (await _registry.HasRowsAsync(GuidV7, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasRowsAsync_IsTrueForALegacyRawTextIdThatHasRows()
    {
        await SeedProjectRowAsync("jsaa");

        (await _registry.HasRowsAsync("jsaa", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>
    ///     The Of()-vs-Scope() gate (ADR-0089 §"Use Of(), never Scope()"): a per-project
    ///     ProjectHasRows built on ProjectRows.Scope() answers true for ANY project-scoped row in
    ///     the bank, since Scope() carries no project_id — so a Scope()-based implementation makes
    ///     B read true too. Only ProjectRows.Of() tells the two projects apart.
    /// </summary>
    [Fact]
    public async Task HasRowsAsync_IsFalseForOneProjectWhileTrueForAnother()
    {
        await SeedProjectRowAsync("project-a");
        await _registry.RegisterAsync("project-b", null, TestContext.Current.CancellationToken);

        (await _registry.HasRowsAsync("project-a", TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await _registry.HasRowsAsync("project-b", TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>
    ///     The #546-review open question: a code-only legacy project has its rows in
    ///     <c>code_entries</c>, never <c>entries</c> — ProjectHasRows must cover both corpora or the
    ///     "rows" half of ADR-0089 decision 3's refusal test refuses a project that genuinely has
    ///     content, just none of it memory-corpus.
    /// </summary>
    [Fact]
    public async Task HasRowsAsync_IsTrueForACodeOnlyLegacyProject_WithRowsOnlyInCodeEntries()
    {
        await SeedCodeEntryRowAsync("code-only-legacy");

        (await _registry.HasRowsAsync("code-only-legacy", TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    private async Task SeedProjectRowAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) "
            + "VALUES (@hash, 'p.md', 'v', 'project', @projectId, 1, 1)",
            new { hash = Guid.NewGuid().ToString("N"), projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedCodeEntryRowAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at) "
            + "VALUES (@hash, 'Foo.cs', 'class Foo {}', 'Foo.cs', 1, 1, @projectId, 1, 1)",
            new { hash = Guid.NewGuid().ToString("N"), projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> CountProjectRowsAsync(string canonicalId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM projects WHERE id = @canonicalId",
            new { canonicalId }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<string?> ReadProjectNameAsync(string canonicalId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM projects WHERE id = @canonicalId",
            new { canonicalId }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task<long> ReadCreatedAtAsync(string canonicalId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT created_at FROM projects WHERE id = @canonicalId",
            new { canonicalId }, cancellationToken: TestContext.Current.CancellationToken));
    }
}
