using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionSchemaTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("decisions-schema");

    [RetryFact]
    public async Task Bank_creates_project_owned_decision_tables()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var tables = (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'decision%'")).ToHashSet(StringComparer.Ordinal);
        tables.ShouldContain("decision_projects");
        tables.ShouldContain("decision_tasks");
        tables.ShouldContain("decisions");
        tables.ShouldContain("decision_versions");
        tables.ShouldContain("decision_edges");
        tables.ShouldContain("decision_requests");
        tables.ShouldContain("decision_imports");
    }

    [RetryFact]
    public async Task Noninitial_versions_require_a_nonnull_change_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using (var connection = await factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync("INSERT INTO projects(id,name,created_at) VALUES('a','A',1)");
        }

        var store = new SqliteDecisionStore(factory, new SharedProjectScope());
        await Should.ThrowAsync<SqliteException>(() => store.WriteAsync("a", async (writer, token) =>
        {
            var now = new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
            await writer.AddTaskAsync(new DecisionTask("task", "Task", 1, now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("decision", "task", 1, now), token);
            await writer.AddVersionAsync(new DecisionVersion("decision", 2, "task", 1,
                new DecisionContent("t", "c", "s", "o", "r"), DecisionStatus.Accepted,
                now, "actor", "session", null), token);
            return true;
        }, ct));
    }

    public void Dispose() => TestData.DeleteTempRoot(_root);
}
