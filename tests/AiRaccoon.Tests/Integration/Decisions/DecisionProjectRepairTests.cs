using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SQLitePCL;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionProjectRepairTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("decision-project-repair");

    private SqliteConnectionFactory Factory()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    private static ProjectIdsRepair Repair() => new(new FakeTimeProvider(), new SharedProjectScope());

    [RetryFact]
    public async Task DecisionRepair_TwoGraphsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory();
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('loser','Loser',1),('winner','Winner',1);
            INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('loser-entry','loser-entry','loser-entry','seed.md','s','project','loser','ctx',1,1,'pending');
            """);
        await DecisionGraphFixture.SeedAsync(factory, connection, "loser", ct);
        await DecisionGraphFixture.SeedAsync(factory, connection, "winner", ct);
        var bank = await DecisionGraphFixture.DumpBankAsync(connection);
        var changes = await DecisionGraphFixture.TotalChangesAsync(connection);

        var error = await Should.ThrowAsync<DecisionGraphRepairException>(() => Repair().ApplyAsync(connection,
            new ProjectIdsFoldPlan([new ProjectIdFold("loser", "winner")], [], [], []), ct));

        error.ProjectId.ShouldBe("loser");
        error.Tables.Order(StringComparer.Ordinal).ShouldBe(DecisionGraphFixture.GraphTables);
        (await DecisionGraphFixture.TotalChangesAsync(connection)).ShouldBe(changes, "refusal precedes every statement that writes");
        (await DecisionGraphFixture.DumpBankAsync(connection)).ShouldBe(bank);
    }

    [RetryFact]
    public async Task DecisionRepair_FoldAndConflictReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory();
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('graph-free','GraphFree',1),('occupied','Occupied',1),('winner','Winner',1);
            INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES ('free-entry','free-entry','free-entry','seed.md','s','project','graph-free','ctx',1,1,'pending');
            INSERT INTO decision_projects(project_id,revision) VALUES('occupied',1);
            INSERT INTO decision_tasks(project_id,task_id,title,registered_order,registered_at) VALUES('occupied','t','T',1,'2026-11-04');
            """);
        await DecisionGraphFixture.SeedAsync(factory, connection, "winner", ct);
        var winnerGraph = await DecisionGraphFixture.DumpGraphAsync(connection, "winner");
        var bank = await DecisionGraphFixture.DumpBankAsync(connection);
        var changes = await DecisionGraphFixture.TotalChangesAsync(connection);

        var conflict = await Should.ThrowAsync<DecisionGraphRepairException>(() => Repair().ApplyAsync(connection,
            new ProjectIdsFoldPlan([new ProjectIdFold("graph-free", "winner"), new ProjectIdFold("occupied", "winner")], [], [], []), ct));

        conflict.ProjectId.ShouldBe("occupied");
        conflict.Tables.Order(StringComparer.Ordinal).ShouldBe(["decision_projects", "decision_tasks"]);
        conflict.Message.ShouldContain("'occupied'");
        (await DecisionGraphFixture.TotalChangesAsync(connection)).ShouldBe(changes, "the graph-free fold waits for the whole plan");
        (await DecisionGraphFixture.DumpBankAsync(connection)).ShouldBe(bank);

        await Repair().ApplyAsync(connection, new ProjectIdsFoldPlan([new ProjectIdFold("graph-free", "winner")], [], [], []), ct);

        (await connection.ExecuteScalarAsync<string>("SELECT project_id FROM entries WHERE hash='free-entry'")).ShouldBe("winner");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM projects WHERE id='graph-free'")).ShouldBe(0);
        (await DecisionGraphFixture.DumpGraphAsync(connection, "winner")).ShouldBe(winnerGraph);
        (await DecisionGraphFixture.DumpGraphAsync(connection, "graph-free")).ShouldBe(await DecisionGraphFixture.DumpGraphAsync(connection, "nobody"));
    }

    [RetryTheory]
    [InlineData("fold")]
    [InlineData("drop")]
    [InlineData("retire")]
    public async Task DecisionDelete_GraphOwnerIsRefusedWithItsWholeGraphIntact(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory();
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('loser','Loser',1),('winner','Winner',1);
            INSERT INTO settings(key,value) VALUES('watch.enabled.loser','true');
            """);
        await DecisionGraphFixture.SeedAsync(factory, connection, "loser", ct);
        var bank = await DecisionGraphFixture.DumpBankAsync(connection);
        var changes = await DecisionGraphFixture.TotalChangesAsync(connection);
        var plan = operation switch
        {
            "fold" => new ProjectIdsFoldPlan([new ProjectIdFold("loser", "winner")], [], [], []),
            "drop" => new ProjectIdsFoldPlan([], ["loser"], [], []),
            _ => new ProjectIdsFoldPlan([], [], ["loser"], [])
        };

        var error = await Should.ThrowAsync<DecisionGraphRepairException>(() => Repair().ApplyAsync(connection, plan, ct));

        error.ProjectId.ShouldBe("loser");
        error.Tables.Order(StringComparer.Ordinal).ShouldBe(DecisionGraphFixture.GraphTables);
        (await DecisionGraphFixture.TotalChangesAsync(connection)).ShouldBe(changes);
        (await DecisionGraphFixture.DumpBankAsync(connection)).ShouldBe(bank);
    }

    [RetryFact]
    public async Task Repair_checks_graph_ownership_under_the_same_write_lock_as_retirement()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("INSERT INTO projects(id,name,created_at) VALUES('loser','Loser',1),('winner','Winner',1)");
        var guardReads = new List<bool>();
        strdelegate_trace tracer = (_, sql) =>
        {
            if (sql.StartsWith("SELECT EXISTS(SELECT 1 FROM decision", StringComparison.Ordinal))
            {
                guardReads.Add(raw.sqlite3_get_autocommit(connection.Handle) == 0);
            }
        };
        raw.sqlite3_trace(connection.Handle, tracer, null);
        try
        {
            await new ProjectIdsRepair(new FakeTimeProvider(), new SharedProjectScope()).ApplyAsync(connection,
                new ProjectIdsFoldPlan([], [], ["loser"], []), ct);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        }

        guardReads.Count.ShouldBe(DecisionSchema.ProjectTableNames.Count());
        guardReads.ShouldAllBe(inTransaction => inTransaction);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM projects WHERE id='loser'")).ShouldBe(0);
    }

    [RetryFact]
    public async Task Census_pins_a_registered_project_that_owns_only_decisions_instead_of_retiring_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('graph-only','GraphOnly',1),('empty','Empty',1);
            INSERT INTO decision_projects(project_id,revision) VALUES('graph-only',0);
            """);

        var census = await ProjectIdCensus.CollectAsync(connection, ct);
        var plan = ProjectIdsFoldPlan.FromCensus(census, ProjectIdAliasMap.Empty);

        census.Row("graph-only").DecisionRows.ShouldBe(1);
        plan.RetiredProjects.ShouldNotContain("graph-only");
        plan.RetiredProjects.ShouldContain("empty");
    }

    public void Dispose() => TestData.DeleteTempRoot(_root);
}
