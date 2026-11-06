using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Shouldly;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Xunit;
using Dapper;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionStoreTests : IAsyncLifetime
{
    private readonly string _root = TestData.CreateTempRoot("decisions-store");
    private static DateTimeOffset Now => new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private SqliteDecisionStore Store()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new SqliteDecisionStore(new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), new SharedProjectScope());
    }

    [RetryFact]
    public async Task History_and_request_receipts_survive_a_new_store_instance()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.SetRevisionAsync(1, token);
            await writer.AddTaskAsync(new DecisionTask("task", "Task", 1, Now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("decision", "task", 1, Now), token);
            await writer.AddVersionAsync(new DecisionVersion("decision", 1, "task", 1,
                new DecisionContent("Title", "architecture", "Scenario", "Outcome", "Why", sources: [new("docs/adr/a.md", "ADR")]),
                DecisionStatus.Accepted, Now, "agent", "session", null), token);
            await writer.AddReceiptAsync("request", new DecisionReceipt("hash", "{}", 1), token);
            return true;
        }, ct);

        await Store().ReadAsync("a", async (reader, token) =>
        {
            (await reader.RevisionAsync(token)).ShouldBe(1);
            (await reader.TasksAsync(token)).Single().Title.ShouldBe("Task");
            (await reader.RecordAsync("decision", token))!.OriginTaskId.ShouldBe("task");
            var version = (await reader.VersionsAsync("decision", token)).Single();
            version.Content.Reasoning.ShouldBe("Why");
            version.Content.Sources.Single().Uri.ShouldBe("docs/adr/a.md");
            version.RecordedAt.ShouldBe(Now);
            (await reader.ReceiptAsync("request", token))!.PayloadHash.ShouldBe("hash");
            return true;
        }, ct);
        await Store().ReadAsync("b", async (reader, token) =>
        {
            (await reader.RecordAsync("decision", token)).ShouldBeNull();
            (await reader.VersionsAsync("decision", token)).ShouldBeEmpty();
            (await reader.ReceiptAsync("request", token)).ShouldBeNull();
            return true;
        }, ct);
    }

    [RetryFact]
    public async Task Failed_write_rolls_back_all_prior_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<InvalidOperationException>(() => Store().WriteAsync<bool>("a", async (writer, token) =>
        {
            await writer.SetRevisionAsync(1, token);
            await writer.AddTaskAsync(new DecisionTask("task", "Task", 1, Now, null), token);
            throw new InvalidOperationException("simulated second write failure");
        }, ct));
        await Store().ReadAsync("a", async (reader, token) =>
        {
            (await reader.RevisionAsync(token)).ShouldBe(0);
            (await reader.TasksAsync(token)).ShouldBeEmpty();
            return true;
        }, ct);
    }

    [RetryFact]
    public async Task Read_transaction_keeps_one_snapshot_while_another_writer_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.SetRevisionAsync(1, token);
            await writer.AddTaskAsync(new DecisionTask("first", "First", 1, Now, null), token);
            return true;
        }, ct);
        await Store().ReadAsync("a", async (reader, token) =>
        {
            (await reader.RevisionAsync(token)).ShouldBe(1);
            await Store().WriteAsync("a", async (writer, writeToken) =>
            {
                await writer.SetRevisionAsync(2, writeToken);
                await writer.AddTaskAsync(new DecisionTask("second", "Second", 2, Now, null), writeToken);
                return true;
            }, token);
            (await reader.TasksAsync(token)).Count.ShouldBe(1);
            (await reader.RevisionAsync(token)).ShouldBe(1);
            return true;
        }, ct);
        (await Store().ReadAsync("a", (reader, token) => reader.RevisionAsync(token), ct)).ShouldBe(2);
    }

    [RetryFact]
    public async Task Composite_foreign_keys_refuse_edges_to_another_projects_decisions()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.AddTaskAsync(new DecisionTask("task", "Task", 1, Now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("a-only", "task", 1, Now), token);
            return true;
        }, ct);
        var error = await Should.ThrowAsync<SqliteException>(() => Store().WriteAsync("b", async (writer, token) =>
        {
            await writer.AddTaskAsync(new DecisionTask("task", "Task", 1, Now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("b-only", "task", 1, Now), token);
            await writer.AddEdgeAsync(new DecisionEdge("edge", "b-only", "a-only", DecisionEdgeKind.DependsOn,
                "dependency", "task", 1, Now, "actor", "session"), token);
            return true;
        }, ct));
        error.SqliteErrorCode.ShouldBe(19);
        (await Store().ReadAsync("b", (reader, token) => reader.RecordsAsync(token), ct)).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task Edge_removal_preserves_its_own_task_reason_and_session_after_reopen()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.SetRevisionAsync(1, token);
            await writer.AddTaskAsync(new DecisionTask("first", "First", 1, Now, null), token);
            await writer.AddTaskAsync(new DecisionTask("second", "Second", 2, Now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("one", "first", 1, Now), token);
            await writer.AddRecordAsync(new DecisionRecord("two", "first", 1, Now), token);
            var edge = new DecisionEdge("edge", "one", "two", DecisionEdgeKind.Supports, "support", "first", 1, Now, "actor-a", "session-a");
            await writer.AddEdgeAsync(edge, token);
            await writer.SetRevisionAsync(2, token);
            await writer.RemoveEdgeAsync(edge with { RemovedRevision = 2, RemovedAt = Now.AddMinutes(1), RemovalTaskId = "second", RemovalReason = "new evidence", RemovalActor = "actor-b", RemovalSessionId = "session-b" }, token);
            await writer.AddImportAsync(new DecisionImport("legacy", "old-id", "one", "first", "digest", "{\"extra\":1}", "{}"), token);
            return true;
        }, ct);
        await Store().ReadAsync("a", async (reader, token) =>
        {
            var edge = (await reader.EdgesAsync(token)).Single();
            edge.TaskId.ShouldBe("first");
            edge.RemovalTaskId.ShouldBe("second");
            edge.RemovalReason.ShouldBe("new evidence");
            edge.RemovalSessionId.ShouldBe("session-b");
            edge.RemovedRevision.ShouldBe(2);
            (await reader.ImportsAsync(token)).Single().RawPayload.ShouldBe("{\"extra\":1}");
            return true;
        }, ct);
    }

    [RetryFact]
    public async Task Stale_project_revision_has_a_conflict_type_instead_of_argument_validation()
    {
        var ct = TestContext.Current.CancellationToken;
        var error = await Should.ThrowAsync<DecisionRevisionConflictException>(() => Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.SetRevisionAsync(2, token);
            return true;
        }, ct));
        error.ExpectedRevision.ShouldBe(1);
        error.CurrentRevision.ShouldBe(0);
    }

    [RetryFact]
    public async Task Unregistered_owner_cannot_start_a_graph_transaction()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<UnregisteredProjectException>(() => Store().WriteAsync("unknown", async (writer, token) =>
        {
            await writer.SetRevisionAsync(1, token);
            return true;
        }, ct));
        (await Store().ReadAsync("unknown", (reader, token) => reader.RevisionAsync(token), ct)).ShouldBe(0);
    }

    [RetryFact]
    public async Task Queued_writer_rechecks_registration_after_retirement_commits()
    {
        var ct = TestContext.Current.CancellationToken;
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var blocker = await factory.OpenBankAsync(ct);
        await blocker.ExecuteAsync("BEGIN IMMEDIATE; DELETE FROM projects WHERE id='a';");
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedFactory = Substitute.For<ISqliteConnectionFactory>();
        queuedFactory.OpenBankAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var connection = await factory.OpenBankAsync(call.Arg<CancellationToken>());
            opened.TrySetResult();
            return connection;
        });
        var store = new SqliteDecisionStore(queuedFactory, new SharedProjectScope());
        var writer = Task.Run(() => store.WriteAsync("a", async (session, token) =>
        {
            await session.AddTaskAsync(new DecisionTask("queued", "Queued", 1, Now, null), token);
            return true;
        }, ct), ct);
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally
        {
            await blocker.ExecuteAsync("COMMIT;");
        }

        await Should.ThrowAsync<UnregisteredProjectException>(() => writer.WaitAsync(TimeSpan.FromSeconds(10), ct));
        (await blocker.ExecuteScalarAsync<int>("SELECT count(*) FROM decision_projects WHERE project_id='a'")).ShouldBe(0);
    }

    [RetryFact]
    public async Task Fts_search_reads_only_current_project_rationale_and_treats_operators_as_text()
    {
        var ct = TestContext.Current.CancellationToken;
        await Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.AddTaskAsync(new DecisionTask("task", "Task", 1, Now, null), token);
            await writer.AddRecordAsync(new DecisionRecord("decision", "task", 1, Now), token);
            await writer.AddVersionAsync(new DecisionVersion("decision", 1, "task", 1,
                new DecisionContent("t", "c", "s", "o", "obsolete rationale"), DecisionStatus.Accepted, Now, "actor", "session", null), token);
            await writer.AddVersionAsync(new DecisionVersion("decision", 2, "task", 2,
                new DecisionContent("t", "c", "s", "o", "fresh rationale"), DecisionStatus.Accepted, Now, "actor", "session", "clarify"), token);
            return true;
        }, ct);
        await Store().ReadAsync("a", async (reader, token) =>
        {
            (await reader.SearchAsync("fresh", token)).Single().DecisionId.ShouldBe("decision");
            (await reader.SearchAsync("obsolete", token)).ShouldBeEmpty();
            (await reader.SearchAsync("OR \"*", token)).ShouldBeEmpty();
            return true;
        }, ct);
        (await Store().ReadAsync("b", (reader, token) => reader.SearchAsync("fresh", token), ct)).ShouldBeEmpty();
    }

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT INTO projects(id,name,created_at) VALUES('a','A',1),('b','B',1)");
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_root);
        return ValueTask.CompletedTask;
    }
}
