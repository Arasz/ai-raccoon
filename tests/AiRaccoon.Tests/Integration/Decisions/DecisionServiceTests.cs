using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Dapper;
using Microsoft.Data.Sqlite;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionServiceTests : IAsyncLifetime
{
    private readonly string _root = TestData.CreateTempRoot("decisions-service");
    private DecisionService Service()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new DecisionService(new SqliteDecisionStore(new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), new SharedProjectScope()),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
    }

    [RetryFact]
    public async Task Registration_and_recording_are_durable_and_replay_safe()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        var task = await service.RegisterTaskAsync("a", new DecisionWrite("task", "session", "register"), "Task", cancellationToken: ct);
        task.ProjectRevision.ShouldBe(1);
        var content = new DecisionContent("Bank storage", "architecture", "Need persistence", "Use bank", "Reuse encryption");
        var write = new DecisionWrite("task", "session", "record");
        var recorded = await service.RecordAsync("a", write, content, DecisionStatus.Accepted, ct);
        var replay = await Service().RecordAsync("a", write, content, DecisionStatus.Accepted, ct);
        replay.Replayed.ShouldBeTrue();
        replay.ProjectRevision.ShouldBe(2);
        replay.Value.DecisionId.ShouldBe(recorded.Value.DecisionId);
        var read = await Service().GetAsync("a", recorded.Value.DecisionId, cancellationToken: ct);
        read.Value.Versions.Single().Content.Reasoning.ShouldBe("Reuse encryption");
        read.ProjectRevision.ShouldBe(2);
        var conflict = await Should.ThrowAsync<DecisionException>(() => service.RecordAsync("a", write, content, DecisionStatus.Proposed, ct));
        conflict.Kind.ShouldBe("decision-request-conflict");
        var unknown = await Should.ThrowAsync<DecisionException>(() => service.GetAsync("b", recorded.Value.DecisionId, cancellationToken: ct));
        unknown.Kind.ShouldBe("decision-not-found");
    }

    [RetryFact]
    public async Task Amendment_replay_precedes_stale_version_checks_and_preserves_history()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("first", "session", "register-first"), "First", cancellationToken: ct);
        var initial = await service.RecordAsync("a", new DecisionWrite("first", "session", "record"), new DecisionContent("Original", "architecture", "s", "o", "old"), DecisionStatus.Accepted, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("second", "session", "register-second"), "Second", cancellationToken: ct);
        var content = new DecisionContent("Amended", "architecture", "s", "o", "new evidence");
        var write = new DecisionWrite("second", "session", "amend");
        var amended = await service.AmendAsync("a", write, initial.Value.DecisionId, 1, "clarify rationale", content, ct);
        amended.Value.Version.ShouldBe(2);
        amended.Value.TaskId.ShouldBe("second");
        var replay = await service.AmendAsync("a", write, initial.Value.DecisionId, 1, "clarify rationale", content, ct);
        replay.Replayed.ShouldBeTrue();
        replay.ProjectRevision.ShouldBe(amended.ProjectRevision);
        var conflict = await Should.ThrowAsync<DecisionException>(() => service.AmendAsync("a", new DecisionWrite("second", "session", "stale"), initial.Value.DecisionId, 1, "stale", content, ct));
        conflict.Kind.ShouldBe("decision-conflict");
        conflict.CurrentVersion.ShouldBe(2);
        var history = await service.GetAsync("a", initial.Value.DecisionId, includeHistory: true, cancellationToken: ct);
        history.Value.Versions.Count.ShouldBe(2);
        history.Value.Versions[0].Content.Reasoning.ShouldBe("old");
        history.Value.Versions[1].ChangeReason.ShouldBe("clarify rationale");
        history.Value.Record.OriginTaskId.ShouldBe("first");
    }

    [RetryFact]
    public async Task Competing_amendments_have_exactly_one_winner()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "session", "register"), "Task", cancellationToken: ct);
        var initial = await service.RecordAsync("a", new DecisionWrite("task", "session", "record"), new DecisionContent("Original", "architecture", "s", "o", "r"), cancellationToken: ct);
        async Task<bool> Amend(string requestId)
        {
            try
            {
                await Service().AmendAsync("a", new DecisionWrite("task", "session", requestId), initial.Value.DecisionId, 1,
                    requestId, new DecisionContent(requestId, "architecture", "s", "o", "r"), ct);
                return true;
            }
            catch (DecisionException exception) when (exception.Kind == "decision-conflict")
            {
                return false;
            }
        }

        (await Task.WhenAll(Amend("one"), Amend("two"))).Count(winner => winner).ShouldBe(1);
        (await service.GetAsync("a", initial.Value.DecisionId, includeHistory: true, cancellationToken: ct)).Value.Versions.Count.ShouldBe(2);
    }

    private async Task<(DecisionService Service, string First, string Second, long Revision)> AcceptedPairAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "session", "register"), "Task", cancellationToken: ct);
        var first = await service.RecordAsync("a", new DecisionWrite("task", "session", "first"), new DecisionContent("First", "architecture", "s", "o", "r"), DecisionStatus.Accepted, ct);
        var second = await service.RecordAsync("a", new DecisionWrite("task", "session", "second"), new DecisionContent("Second", "architecture", "s", "o", "r"), DecisionStatus.Accepted, ct);
        return (service, first.Value.DecisionId, second.Value.DecisionId, second.ProjectRevision);
    }

    [RetryFact]
    public async Task Terminal_transition_preserves_rationale_and_refuses_later_amendment()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, _, _) = await AcceptedPairAsync();
        var deprecated = await service.TransitionAsync("a", new DecisionWrite("task", "session", "deprecate"), first, 1, DecisionStatus.Deprecated, "no longer applicable", ct);
        deprecated.Value.Status.ShouldBe(DecisionStatus.Deprecated);
        deprecated.Value.Content.Title.ShouldBe("First");
        var error = await Should.ThrowAsync<DecisionException>(() => service.AmendAsync("a", new DecisionWrite("task", "session", "amend-terminal"), first, 2, "rewrite", new DecisionContent("Changed", "c", "s", "o", "r"), ct));
        error.Kind.ShouldBe("decision-invalid-transition");
        await Should.ThrowAsync<DecisionException>(() => service.TransitionAsync("a", new DecisionWrite("task", "session", "revive"), first, 2, DecisionStatus.Accepted, "revive", ct));
    }

    [RetryFact]
    public async Task Links_are_owned_cycle_checked_revision_guarded_and_removable_with_provenance()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        var edge = await service.LinkAsync("a", new DecisionWrite("task", "session", "link"), first, second, DecisionEdgeKind.DependsOn, "prerequisite", revision, ct);
        edge.Value.SourceId.ShouldBe(first);
        edge.Value.TargetId.ShouldBe(second);
        var cycle = await Should.ThrowAsync<DecisionException>(() => service.LinkAsync("a", new DecisionWrite("task", "session", "cycle"), second, first, DecisionEdgeKind.DependsOn, "cycle", edge.ProjectRevision, ct));
        cycle.Kind.ShouldBe("decision-invalid-link");
        var stale = await Should.ThrowAsync<DecisionRevisionConflictException>(() => service.UnlinkAsync("a", new DecisionWrite("task", "session", "stale-remove"), edge.Value.EdgeId, "remove", revision, ct));
        stale.CurrentRevision.ShouldBe(edge.ProjectRevision);
        await service.RegisterTaskAsync("a", new DecisionWrite("remove-task", "session", "register-remove"), "Remove task", cancellationToken: ct);
        var removed = await service.UnlinkAsync("a", new DecisionWrite("remove-task", "remover", "remove"), edge.Value.EdgeId, "new evidence", edge.ProjectRevision + 1, ct);
        removed.Value.RemovalTaskId.ShouldBe("remove-task");
        removed.Value.RemovalSessionId.ShouldBe("remover");
        removed.Value.RemovalReason.ShouldBe("new evidence");
        removed.Value.RemovedRevision.ShouldBe(removed.ProjectRevision);
    }

    [RetryFact]
    public async Task Supersession_is_one_revision_and_keeps_its_edge_permanent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        var write = new DecisionWrite("task", "session", "supersede");
        var result = await service.SupersedeAsync("a", write, second, first, 1, 1, "new choice", ct);
        result.ProjectRevision.ShouldBe(revision + 1);
        result.Value.Replaced.Status.ShouldBe(DecisionStatus.Superseded);
        result.Value.Replaced.Version.ShouldBe(2);
        result.Value.Replacement.Status.ShouldBe(DecisionStatus.Accepted);
        result.Value.Edge.SourceId.ShouldBe(second);
        result.Value.Edge.TargetId.ShouldBe(first);
        var replay = await service.SupersedeAsync("a", write, second, first, 1, 1, "new choice", ct);
        replay.Replayed.ShouldBeTrue();
        replay.ProjectRevision.ShouldBe(result.ProjectRevision);
        var removal = await Should.ThrowAsync<DecisionException>(() => service.UnlinkAsync("a", new DecisionWrite("task", "session", "remove-permanent"), result.Value.Edge.EdgeId, "remove", result.ProjectRevision, ct));
        removal.Kind.ShouldBe("decision-invalid-transition");
    }

    [RetryFact]
    public async Task Supersession_failure_rolls_back_edge_version_revision_and_retry_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "memory.db")};Pooling=false");
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync("CREATE TRIGGER fail_supersession BEFORE INSERT ON decision_versions WHEN NEW.status='Superseded' BEGIN SELECT RAISE(ABORT,'injected version failure'); END;");
        var write = new DecisionWrite("task", "session", "supersede");
        await Should.ThrowAsync<SqliteException>(() => service.SupersedeAsync("a", write, second, first, 1, 1, "new choice", ct));
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM decision_edges")).ShouldBe(0);
        var current = await service.GetAsync("a", first, cancellationToken: ct);
        current.ProjectRevision.ShouldBe(revision);
        current.Value.Versions.Single().Status.ShouldBe(DecisionStatus.Accepted);
        await connection.ExecuteAsync("DROP TRIGGER fail_supersession");
        var retried = await service.SupersedeAsync("a", write, second, first, 1, 1, "new choice", ct);
        retried.Replayed.ShouldBeFalse();
        retried.ProjectRevision.ShouldBe(revision + 1);
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
