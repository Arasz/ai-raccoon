using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Shouldly;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionStoreTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("decisions-store");
    private static DateTimeOffset Now => new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private SqliteDecisionStore Store()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new SqliteDecisionStore(new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), new SharedProjectScope());
    }

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    [Fact]
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

    public void Dispose() => TestData.DeleteTempRoot(_root);
}
