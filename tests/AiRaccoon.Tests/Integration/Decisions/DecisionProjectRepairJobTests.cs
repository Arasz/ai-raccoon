using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Projects;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

/// <summary>A decision-only project must not stall the maintenance repair of unrelated ids.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class DecisionProjectRepairJobTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = TestData.CreateTempRoot("decision-project-repair-job");

    [RetryFact]
    public async Task Repair_request_folds_unrelated_ids_and_finishes_when_a_project_owns_only_decisions()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('graph-only','GraphOnly',1),('winner','Winner',1);
            INSERT INTO decision_projects(project_id,revision) VALUES('graph-only',0);
            INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('fold-1','fold-1','fold-1','seed.md','s','project','loser','ctx',1,1,'pending');
            """);
        var map = new ProjectIdAliasMap([new ProjectIdAliasEntry("loser", "winner")], ["winner"], []);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = map.ToJson() },
            cancellationToken: ct));
        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(),
            new FakeTimeProvider(FixedNow), TestData.SharedScope);

        (await job.RunAsync(connection, ct)).ShouldBeTrue("the unrelated fold moves the loser's entry");

        (await connection.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash='fold-1'")).ShouldBe("winner");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM projects WHERE id='graph-only'")).ShouldBe(1);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM decision_projects WHERE project_id='graph-only'")).ShouldBe(1);
        (await job.HasWorkAsync(connection, ct)).ShouldBeFalse("the request is stamped finished");
        (await job.RunAsync(connection, ct)).ShouldBeFalse("the next pass finds no open request and does not throw");
    }

    [RetryFact]
    public async Task Repair_request_holds_back_a_graph_owning_loser_with_entries_and_finishes_the_rest()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('graph-loser','GraphLoser',1),('winner','Winner',1);
            INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('fold-1','fold-1','fold-1','seed.md','s','project','loser','ctx',1,1,'pending'),
                       ('held-1','held-1','held-1','seed.md','s','project','graph-loser','ctx',1,1,'pending');
            """);
        await DecisionGraphFixture.SeedAsync(factory, connection, "graph-loser", ct);
        var graph = await DecisionGraphFixture.DumpGraphAsync(connection, "graph-loser");
        var map = new ProjectIdAliasMap(
            [new ProjectIdAliasEntry("loser", "winner"), new ProjectIdAliasEntry("graph-loser", "winner")], ["winner"], []);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = map.ToJson() },
            cancellationToken: ct));
        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(),
            new FakeTimeProvider(FixedNow), TestData.SharedScope);

        (await job.RunAsync(connection, ct)).ShouldBeTrue("the unrelated fold moves the loser's entry");

        (await connection.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash='fold-1'")).ShouldBe("winner");
        (await connection.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash='held-1'")).ShouldBe("graph-loser");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM projects WHERE id='graph-loser'")).ShouldBe(1);
        (await DecisionGraphFixture.DumpGraphAsync(connection, "graph-loser")).ShouldBe(graph);
        ProjectIdAliasMap.Default.Apply("loser").ShouldBe(new FoldedProjectId("winner", false), "the applied alias reaches the gate");
        ProjectIdAliasMap.Default.Apply("graph-loser").ShouldBe(new FoldedProjectId("graph-loser", false), "a held-back alias must not reach the gate");
        (await Gate().RequireAsync("graph-loser", AccessRequirement.Read, ToolNames.GetDecision, ct)).ShouldBe("graph-loser");
        (await ProjectIdAliases.LoadAsync(connection, ct)).Aliases.Select(a => a.Alias).ShouldBe(["loser"]);
        (await job.HasWorkAsync(connection, ct)).ShouldBeFalse("the request is stamped finished");
        (await job.RunAsync(connection, ct)).ShouldBeFalse("the next pass finds no open request and does not throw");
    }

    [RetryFact]
    public async Task Repair_request_holds_back_a_dropped_graph_owner_and_keeps_it_reachable()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('graph-dropped','GraphDropped',1),('winner','Winner',1);
            INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('fold-1','fold-1','fold-1','seed.md','s','project','loser','ctx',1,1,'pending'),
                       ('held-1','held-1','held-1','seed.md','s','project','graph-dropped','ctx',1,1,'pending');
            """);
        await DecisionGraphFixture.SeedAsync(factory, connection, "graph-dropped", ct);
        var graph = await DecisionGraphFixture.DumpGraphAsync(connection, "graph-dropped");
        var map = new ProjectIdAliasMap([new ProjectIdAliasEntry("loser", "winner")], ["winner"], ["graph-dropped"]);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = map.ToJson() },
            cancellationToken: ct));
        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(),
            new FakeTimeProvider(FixedNow), TestData.SharedScope);

        (await job.RunAsync(connection, ct)).ShouldBeTrue("the unrelated fold moves the loser's entry");

        (await connection.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash='held-1'")).ShouldBe("graph-dropped");
        (await DecisionGraphFixture.DumpGraphAsync(connection, "graph-dropped")).ShouldBe(graph);
        ProjectIdAliasMap.Default.Apply("graph-dropped").ShouldBe(new FoldedProjectId("graph-dropped", false), "a held-back drop must not retire the id");
        (await Gate().RequireAsync("graph-dropped", AccessRequirement.Read, ToolNames.GetDecision, ct)).ShouldBe("graph-dropped");
        (await ProjectIdAliases.LoadAsync(connection, ct)).Dropped.ShouldBeEmpty();
        (await job.HasWorkAsync(connection, ct)).ShouldBeFalse("the request is stamped finished");
    }

    /// <summary>The real gate over permissive access and registration, so only the alias fold can refuse.</summary>
    private static ToolGate Gate() =>
        new(new AllowAllGuard(), new FakePromotionQueue(), new NeverMigratingStore(), new AllowAllRegistration(), TestData.ScopeCheck);

    private sealed class AllowAllGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAllRegistration : IProjectRegistrationGuard
    {
        public Task EnsureAsync(string projectId, AccessRequirement requirement, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public void Dispose()
    {
        ProjectIdAliasMap.ResetDefault();
        TestData.DeleteTempRoot(_root);
    }
}
