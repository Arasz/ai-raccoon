using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>The reserved name <c>shared</c> is refused by the registry itself, whichever path registers or renames a project.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ReservedNameRegistryTests : IAsyncLifetime
{
    private const string Fresh = "0199a1b2-0000-7000-8000-00000000001f";

    private readonly string _dataRoot = TestData.CreateTempRoot("reserved-name-registry");
    private SqliteMemoryStore _store = null!;
    private SqliteProjectDirectory _directory = null!;

    public ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(factory), new StubChunker(), TimeProvider.System,
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _directory = new SqliteProjectDirectory(factory, _store, NullLogger<SqliteProjectDirectory>.Instance,
            TestData.SharedScope);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [RetryFact]
    public async Task Store_RegisteringTheReservedName_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ReservedProjectNameException>(() => _store.RegisterAsync(Fresh, "shared", ct));

        (await _store.IsRegisteredAsync(Fresh, ct)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task Directory_RegisteringTheReservedName_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<ReservedProjectNameException>(() => _directory.RegisterAsync(Fresh, "shared", ct));

        (await _store.IsRegisteredAsync(Fresh, ct)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task RenamingAnExistingProjectToTheReservedName_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.RegisterAsync(Fresh, "acme", ct);

        await Should.ThrowAsync<ReservedProjectNameException>(() => _directory.RegisterAsync(Fresh, "shared", ct));

        (await _directory.FindByNameAsync("acme", ct)).ShouldBe([Fresh]);
    }

    [RetryFact]
    public async Task ANameThatIsNotTheReservedLiteral_StillRegisters()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.RegisterAsync(Fresh, "Shared", ct);

        (await _directory.FindByNameAsync("Shared", ct)).ShouldBe([Fresh]);
    }
}
