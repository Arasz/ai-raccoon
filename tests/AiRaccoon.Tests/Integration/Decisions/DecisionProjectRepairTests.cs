using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionProjectRepairTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("decision-project-repair");

    [RetryTheory]
    [InlineData("fold")]
    [InlineData("drop")]
    [InlineData("retire")]
    public async Task Graph_owners_block_every_retirement_path_before_any_mutation(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("""
            INSERT INTO projects(id,name,created_at) VALUES('loser','Loser',1),('winner','Winner',1);
            INSERT INTO decision_projects(project_id,revision) VALUES('loser',0),('winner',0);
            INSERT INTO settings(key,value) VALUES('watch.enabled.loser','true');
            """);
        var plan = operation switch
        {
            "fold" => new ProjectIdsFoldPlan([new ProjectIdFold("loser", "winner")], [], [], []),
            "drop" => new ProjectIdsFoldPlan([], ["loser"], [], []),
            _ => new ProjectIdsFoldPlan([], [], ["loser"], [])
        };
        var error = await Should.ThrowAsync<DecisionGraphRepairException>(() =>
            new ProjectIdsRepair(new FakeTimeProvider(), new SharedProjectScope()).ApplyAsync(connection, plan, ct));
        error.ProjectId.ShouldBe("loser");
        error.Tables.ShouldContain("decision_projects");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM projects WHERE id IN ('loser','winner')")).ShouldBe(2);
        (await connection.ExecuteScalarAsync<string>("SELECT value FROM settings WHERE key='watch.enabled.loser'")).ShouldBe("true");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM decision_projects")).ShouldBe(2);
    }

    public void Dispose() => TestData.DeleteTempRoot(_root);
}
