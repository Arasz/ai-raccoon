using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Projects;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     The server's project directory over a real bank: exact name lookup, register outcomes
///     (alias, retired, raw text, row-holding), first-non-null-name-wins, and check statuses.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class SqliteProjectDirectoryTests : IAsyncLifetime
{
    private const string Winner = "0199a1b2-0000-7000-8000-00000000000a";
    private const string AliasSpelling = "{0199A1B2-0000-7000-8000-00000000000B}";
    private const string Alias = "0199a1b2-0000-7000-8000-00000000000b";
    private const string Dropped = "0199a1b2-0000-7000-8000-00000000000d";
    private const string Fresh = "0199a1b2-0000-7000-8000-00000000000f";

    private readonly string _dataRoot = TestData.CreateTempRoot("sqlite-project-directory");
    private readonly FakeLogger<SqliteProjectDirectory> _logger = new();
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private SqliteProjectDirectory _directory = null!;

    private IProjectRegistry Registry => _store;

    public ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), TimeProvider.System,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _directory = new SqliteProjectDirectory(_factory, _store, _logger);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [RetryFact]
    public async Task FindByName_IsExactCaseSensitiveAndTreatsPercentLiterally()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync("lower", "acme", ct);
        await Registry.RegisterAsync("upper", "Acme", ct);
        await Registry.RegisterAsync("underscore", "a_me", ct);

        (await _directory.FindByNameAsync("acme", ct)).ShouldBe(["lower"]);
        (await _directory.FindByNameAsync("ACME", ct)).ShouldBeEmpty();
        (await _directory.FindByNameAsync("a%", ct)).ShouldBeEmpty();
        (await _directory.FindByNameAsync("a_me", ct)).ShouldBe(["underscore"]);
    }

    [RetryFact]
    public async Task FindByName_SeveralMatches_ReturnsEveryIdInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync("b-project", "shared", ct);
        await Registry.RegisterAsync("a-project", "shared", ct);

        (await _directory.FindByNameAsync("shared", ct)).ShouldBe(["a-project", "b-project"]);
    }

    [RetryFact]
    public async Task FindByName_RawTextId_ReturnedAsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync("Vue-Kanban", "vue-kanban", ct);

        (await _directory.FindByNameAsync("vue-kanban", ct)).ShouldBe(["Vue-Kanban"]);
    }

    [RetryFact]
    public async Task Register_SecondNonNullName_KeepsTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _directory.RegisterAsync(Fresh, "first", ct)).Outcome.ShouldBe(ProjectRegistrationOutcome.Registered);
        (await _directory.RegisterAsync(Fresh, "second", ct)).Outcome.ShouldBe(ProjectRegistrationOutcome.AlreadyRegistered);

        (await _directory.FindByNameAsync("first", ct)).ShouldBe([Fresh]);
        (await _directory.FindByNameAsync("second", ct)).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Register_LaterNameFillsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _directory.RegisterAsync(Fresh, null, ct)).Outcome.ShouldBe(ProjectRegistrationOutcome.Registered);
        (await _directory.RegisterAsync(Fresh, "acme", ct)).Outcome.ShouldBe(ProjectRegistrationOutcome.AlreadyRegistered);

        (await _directory.FindByNameAsync("acme", ct)).ShouldBe([Fresh]);
    }

    [RetryFact]
    public async Task Register_UnknownGuidSpelling_RegistersTheCanonicalId()
    {
        var ct = TestContext.Current.CancellationToken;

        var registration = await _directory.RegisterAsync(Fresh.ToUpperInvariant(), null, ct);

        registration.ShouldBe(new ProjectRegistration(Fresh, ProjectRegistrationOutcome.Registered));
        (await Registry.IsRegisteredAsync(Fresh, ct)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task Register_AlreadyRegisteredRawText_IsAlreadyRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync("ai-badger", null, ct);

        (await _directory.RegisterAsync("ai-badger", null, ct))
            .ShouldBe(new ProjectRegistration("ai-badger", ProjectRegistrationOutcome.AlreadyRegistered));
    }

    [RetryFact]
    public async Task Register_AliasOfRegistered_IsAlreadyRegisteredUnderWinner_NoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync(Winner, null, ct);
        LoadMap();

        var registration = await _directory.RegisterAsync(AliasSpelling, "acme", ct);

        registration.ShouldBe(new ProjectRegistration(Winner, ProjectRegistrationOutcome.AlreadyRegistered));
        (await ProjectRowCountAsync(Alias)).ShouldBe(0);
    }

    [RetryFact]
    public async Task Register_Dropped_IsRetired_NoRow()
    {
        var ct = TestContext.Current.CancellationToken;
        LoadMap();

        var registration = await _directory.RegisterAsync(Dropped.ToUpperInvariant(), null, ct);

        registration.ShouldBe(new ProjectRegistration(Dropped, ProjectRegistrationOutcome.Retired));
        (await ProjectRowCountAsync(Dropped)).ShouldBe(0);
    }

    [RetryFact]
    public async Task Register_UnknownRawText_IsNotAGuid_NoRow()
    {
        var ct = TestContext.Current.CancellationToken;

        var registration = await _directory.RegisterAsync("acme-raw", null, ct);

        registration.ShouldBe(new ProjectRegistration("acme-raw", ProjectRegistrationOutcome.NotAGuid));
        (await ProjectRowCountAsync("acme-raw")).ShouldBe(0);
    }

    /// <summary>The nil guid parses, so it would otherwise slip through every guid check; it names nothing and is refused without a row.</summary>
    [RetryTheory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("{00000000-0000-0000-0000-000000000000}")]
    public async Task Register_NilGuid_IsNotAGuid_NoRow(string spelling)
    {
        var ct = TestContext.Current.CancellationToken;

        var registration = await _directory.RegisterAsync(spelling, "acme", ct);

        registration.ShouldBe(new ProjectRegistration("00000000-0000-0000-0000-000000000000", ProjectRegistrationOutcome.NotAGuid));
        (await ProjectRowCountAsync("00000000-0000-0000-0000-000000000000")).ShouldBe(0);
    }

    [RetryFact]
    public async Task Register_RowHoldingRawText_IsRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEntryAsync("legacy-raw");

        var registration = await _directory.RegisterAsync("legacy-raw", "legacy", ct);

        registration.ShouldBe(new ProjectRegistration("legacy-raw", ProjectRegistrationOutcome.Registered));
        (await _directory.FindByNameAsync("legacy", ct)).ShouldBe(["legacy-raw"]);
    }

    [RetryFact]
    public async Task Register_LogsEventId694()
    {
        var ct = TestContext.Current.CancellationToken;

        await _directory.RegisterAsync(Fresh, null, ct);
        await _directory.RegisterAsync(Fresh, null, ct);

        var records = _logger.Collector.GetSnapshot().Where(record => record.Id.Id == 694).ToList();
        records.Count.ShouldBe(1, "a registration is logged once; an already-registered id writes nothing new");
        records[0].Message.ShouldContain(Fresh);
    }

    [RetryFact]
    public async Task Check_RegisteredRawText_IsKnown()
    {
        var ct = TestContext.Current.CancellationToken;
        await Registry.RegisterAsync("ai-badger", null, ct);

        (await _directory.CheckAsync("ai-badger", ct)).ShouldBe(new ProjectIdCheck("ai-badger", ProjectIdStatus.Known));
    }

    [RetryFact]
    public async Task Check_FoldsToRowHoldingWinner_IsKnown()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedEntryAsync(Winner);
        LoadMap();

        (await _directory.CheckAsync(AliasSpelling, ct)).ShouldBe(new ProjectIdCheck(Winner, ProjectIdStatus.Known));
    }

    [RetryFact]
    public async Task Check_Unknown()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _directory.CheckAsync(Fresh.ToUpperInvariant(), ct)).ShouldBe(new ProjectIdCheck(Fresh, ProjectIdStatus.Unknown));
        (await _directory.CheckAsync("acme-raw", ct)).ShouldBe(new ProjectIdCheck("acme-raw", ProjectIdStatus.Unknown));
        (await ProjectRowCountAsync(Fresh)).ShouldBe(0, "check never registers");
    }

    [RetryFact]
    public async Task Check_Retired()
    {
        var ct = TestContext.Current.CancellationToken;
        LoadMap();

        (await _directory.CheckAsync(Dropped, ct)).ShouldBe(new ProjectIdCheck(Dropped, ProjectIdStatus.Retired));
    }

    private static void LoadMap() =>
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap([new ProjectIdAliasEntry(Alias, Winner)], [Winner], [Dropped]));

    private async Task<long> ProjectRowCountAsync(string id)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM projects WHERE id = @id", new { id },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedEntryAsync(string projectId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES (@hash, 'seed.md', 'seed', 'project', @projectId, 0, 0)",
            new { hash = $"seed-{projectId}", projectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }
}
