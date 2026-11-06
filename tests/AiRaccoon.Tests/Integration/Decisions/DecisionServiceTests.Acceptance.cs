using System.Globalization;
using System.Text;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

public sealed partial class DecisionServiceTests
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [RetryFact]
    public async Task DecisionSelection_LastUsesRegistrationOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("b-old", "s", "register-old"), "Old", cancellationToken: ct);
        var decision = await service.RecordAsync("a", new DecisionWrite("b-old", "s", "record-old"), new DecisionContent("Old", "c", "s", "o", "r"), cancellationToken: ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("a-new", "s", "register-new"), "New", cancellationToken: ct);
        await service.RecordAsync("a", new DecisionWrite("a-new", "s", "record-new"), new DecisionContent("New", "c", "s", "o", "r"), cancellationToken: ct);
        await service.AmendAsync("a", new DecisionWrite("b-old", "s", "edit-old"), decision.Value.DecisionId, 1, "edit", new DecisionContent("Edited", "c", "s", "o", "r"), ct);

        (await service.ExportAsync("a", cancellationToken: ct)).Value.SelectedTaskIds.ShouldBe(["a-new"]);
        (await service.ExportAsync("a", new DecisionTaskSelector(last: 2), ct)).Value.SelectedTaskIds.ShouldBe(["a-new", "b-old"]);
        (await service.ExportAsync("a", new DecisionTaskSelector(all: true), ct)).Value.Tasks.Select(task => task.RegisteredOrder).ShouldBe([2L, 1L]);
    }

    [RetryFact]
    public async Task DecisionSelection_EmptyNewestTaskRemains()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, _, _) = await AcceptedPairAsync();
        await service.RegisterTaskAsync("a", new DecisionWrite("empty", "s", "register-empty"), "Empty newest", cancellationToken: ct);
        await service.AmendAsync("a", new DecisionWrite("task", "s", "amend-old"), first, 1, "clarify", new DecisionContent("Amended", "c", "s", "o", "r"), ct);

        var latest = (await service.ExportAsync("a", cancellationToken: ct)).Value;
        latest.SelectedTaskIds.ShouldBe(["empty"]);
        latest.Decisions.ShouldBeEmpty();
        latest.Counts.Tasks.ShouldBe(1);
        (await service.ExportAsync("a", new DecisionTaskSelector(last: 2), ct)).Value.SelectedTaskIds.ShouldBe(["empty", "task"]);
        var all = (await service.ExportAsync("a", new DecisionTaskSelector(all: true), ct)).Value;
        all.SelectedTaskIds.ShouldBe(["empty", "task"]);
        all.Counts.Decisions.ShouldBe(2);
        var preview = (await service.PreviewAsync("a", cancellationToken: ct)).Value;
        preview.Total.Tasks.ShouldBe(1);
        preview.Total.Decisions.ShouldBe(0);
        preview.Text.ShouldContain("Task empty: Empty newest");
        (await service.ExportAsync("b", cancellationToken: ct)).Value.Counts.Tasks.ShouldBe(0);
    }

    [RetryFact]
    public async Task DecisionSelection_RevisionAndEdgeTouchCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, _) = await AcceptedPairAsync();
        await service.RegisterTaskAsync("a", new DecisionWrite("touch", "s", "register-touch"), "Touch", cancellationToken: ct);
        var amended = await service.AmendAsync("a", new DecisionWrite("touch", "s", "amend"), first, 1, "revise", new DecisionContent("Revised", "c", "s", "o", "r"), ct);
        var edge = await service.LinkAsync("a", new DecisionWrite("touch", "s", "link"), second, first, DecisionEdgeKind.DependsOn, "needs", amended.ProjectRevision, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("remove", "s", "register-remove"), "Remove", cancellationToken: ct);
        await service.UnlinkAsync("a", new DecisionWrite("remove", "s", "unlink"), edge.Value.EdgeId, "obsolete", edge.ProjectRevision + 1, ct);

        var touched = (await service.ExportAsync("a", new DecisionTaskSelector(taskIds: ["touch"]), ct)).Value;
        touched.Counts.Decisions.ShouldBe(2);
        var byId = touched.Decisions.ToDictionary(decision => decision.Record.DecisionId, StringComparer.Ordinal);
        byId[first].Touches.ShouldHaveSingleItem().ShouldBe(new DecisionTouch("touch", "version", 2));
        byId[second].Touches.ShouldHaveSingleItem().ShouldBe(new DecisionTouch("touch", "edge-created", EdgeId: edge.Value.EdgeId));

        var removal = (await service.ExportAsync("a", cancellationToken: ct)).Value;
        removal.SelectedTaskIds.ShouldBe(["remove"]);
        removal.Counts.Decisions.ShouldBe(1);
        removal.Decisions.Single().Record.DecisionId.ShouldBe(second);
        removal.Decisions.Single().Touches.ShouldHaveSingleItem().ShouldBe(new DecisionTouch("remove", "edge-removed", EdgeId: edge.Value.EdgeId));
        (await service.QueryAsync("a", new DecisionQuery(Selector: new DecisionTaskSelector(taskIds: ["touch"])), ct)).Value.TotalMatched.ShouldBe(2);
    }

    [RetryFact]
    public async Task DecisionExport_BoundaryClosureAndHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("original", "s", "register-original"), "Original", cancellationToken: ct);
        var decision = await service.RecordAsync("a", new DecisionWrite("original", "s", "record"), new DecisionContent("One", "c", "s", "o", "first reason"), DecisionStatus.Accepted, ct);
        var outside = await service.RecordAsync("a", new DecisionWrite("original", "s", "record-outside"), new DecisionContent("Outside", "c", "s", "o", "r"), DecisionStatus.Accepted, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("middle", "s", "register-middle"), "Middle", cancellationToken: ct);
        var amended = await service.AmendAsync("a", new DecisionWrite("middle", "s", "amend-middle"), decision.Value.DecisionId, 1, "second", new DecisionContent("Two", "c", "s", "o", "second reason"), ct);
        await service.LinkAsync("a", new DecisionWrite("middle", "s", "link"), decision.Value.DecisionId, outside.Value.DecisionId, DecisionEdgeKind.DependsOn, "needs", amended.ProjectRevision, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("latest", "s", "register-latest"), "Latest", cancellationToken: ct);
        await service.AmendAsync("a", new DecisionWrite("latest", "s", "amend-latest"), decision.Value.DecisionId, 2, "third", new DecisionContent("Three", "c", "s", "o", "third reason"), ct);
        await Store().WriteAsync("a", async (writer, token) =>
        {
            await writer.AddImportAsync(new DecisionImport("fixture", "external", decision.Value.DecisionId, "original", "digest", "{\"extra\":true}", "{}"), token);
            return true;
        }, ct);

        var export = (await service.ExportAsync("a", new DecisionTaskSelector(taskIds: ["middle"]), ct)).Value;
        var full = export.Decisions.Select(item => item.Record.DecisionId).ToHashSet(StringComparer.Ordinal);
        full.ShouldBe([decision.Value.DecisionId]);
        var boundary = export.BoundaryNodes.ShouldHaveSingleItem();
        boundary.DecisionId.ShouldBe(outside.Value.DecisionId);
        boundary.Boundary.ShouldBeTrue();
        export.Edges.ShouldNotBeEmpty();
        foreach (var edge in export.Edges)
        {
            (full.Contains(edge.SourceId) || export.BoundaryNodes.Any(node => node.DecisionId == edge.SourceId)).ShouldBeTrue(edge.SourceId);
            (full.Contains(edge.TargetId) || export.BoundaryNodes.Any(node => node.DecisionId == edge.TargetId)).ShouldBeTrue(edge.TargetId);
        }

        var exported = export.Decisions.Single();
        exported.Versions.Select(version => version.Content.Reasoning).ShouldBe(["first reason", "second reason", "third reason"]);
        exported.Record.CurrentVersion.ShouldBe(3);
        export.Counts.Versions.ShouldBe(3);
        export.Counts.BoundaryNodes.ShouldBe(1);
        export.ReferencedTasks.Select(task => task.TaskId).Order(StringComparer.Ordinal).ShouldBe(["latest", "original"]);
        export.ImportRecords.Single().RawPayload.ShouldBe("{\"extra\":true}");
    }

    [RetryFact]
    public async Task DecisionExport_ConcurrentWriterSingleSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, first, _, revision) = await AcceptedPairAsync();
        var writer = Service();
        var wrote = false;
        async Task WriteBetweenReads()
        {
            if (wrote)
            {
                return;
            }

            wrote = true;
            await writer.RegisterTaskAsync("a", new DecisionWrite("late", "s", "register-late"), "Late", cancellationToken: ct);
            await writer.RecordAsync("a", new DecisionWrite("late", "s", "record-late"), new DecisionContent("Late", "c", "s", "o", "r"), cancellationToken: ct);
            await writer.AmendAsync("a", new DecisionWrite("late", "s", "amend-late"), first, 1, "late edit", new DecisionContent("Late edit", "c", "s", "o", "r"), ct);
        }

        var interleaved = new DecisionService(new InterleavedStore(Store(), WriteBetweenReads), new FakeTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
        var result = await interleaved.ExportAsync("a", new DecisionTaskSelector(all: true), ct);

        wrote.ShouldBeTrue();
        result.ProjectRevision.ShouldBe(revision);
        result.Value.SelectedTaskIds.ShouldBe(["task"]);
        result.Value.Counts.Decisions.ShouldBe(2);
        result.Value.Counts.Versions.ShouldBe(2);
        result.Value.Decisions.Single(decision => decision.Record.DecisionId == first).Record.CurrentVersion.ShouldBe(1);
        (await writer.ExportAsync("a", new DecisionTaskSelector(all: true), ct)).Value.Counts.Versions.ShouldBe(4);
    }

    [RetryFact]
    public async Task DecisionPreview_CountsAndUnicodeBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.RegisterTaskAsync("a", new DecisionWrite("task", "s", "register"), "Task", cancellationToken: ct);
        var paws = string.Concat(Enumerable.Repeat("🐾", 300));
        var ids = new List<string>();
        long revision = 0;
        for (var i = 0; i < 4; i++)
        {
            var recorded = await service.RecordAsync("a", new DecisionWrite("task", "s", "record-" + i.ToString(CultureInfo.InvariantCulture)), new DecisionContent("Paw " + i.ToString(CultureInfo.InvariantCulture), "c", "s", paws, paws), cancellationToken: ct);
            ids.Add(recorded.Value.DecisionId);
            revision = recorded.ProjectRevision;
        }

        await service.LinkAsync("a", new DecisionWrite("task", "s", "link"), ids[0], ids[1], DecisionEdgeKind.DependsOn, "needs", revision, ct);

        var preview = (await service.PreviewAsync("a", new DecisionPreviewOptions(MaxChars: 2000), ct)).Value;
        preview.Text.EnumerateRunes().Count().ShouldBeLessThanOrEqualTo(2000);
        Should.NotThrow(() => StrictUtf8.GetByteCount(preview.Text));
        preview.Total.ShouldBe(new DecisionPreviewCounts(1, 4, 0, 1));
        preview.Shown.Decisions.ShouldBeGreaterThan(0);
        preview.Omitted.Decisions.ShouldBeGreaterThan(0);
        preview.Omitted.ShouldBe(new DecisionPreviewCounts(preview.Total.Tasks - preview.Shown.Tasks, preview.Total.Decisions - preview.Shown.Decisions,
            preview.Total.BoundaryNodes - preview.Shown.BoundaryNodes, preview.Total.Edges - preview.Shown.Edges));
        preview.Text.ShouldContain(FormattableString.Invariant($"decisions {preview.Shown.Decisions}/4"));
        preview.Text.ShouldContain(FormattableString.Invariant($"omitted tasks {preview.Omitted.Tasks}, decisions {preview.Omitted.Decisions}, boundary nodes 0, edges {preview.Omitted.Edges}"));
        preview.AbbreviatedFields.ShouldBe(preview.Shown.Decisions * 2);
        var outcome = preview.Text.Split('\n').First(line => line.StartsWith("  Outcome:", StringComparison.Ordinal))[11..];
        outcome.EnumerateRunes().Count().ShouldBe(240);
    }

    [RetryFact]
    public async Task DecisionEdgeRemoval_TaskProvenanceSurvivesRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (service, first, second, revision) = await AcceptedPairAsync();
        var edge = await service.LinkAsync("a", new DecisionWrite("task", "creator", "link"), first, second, DecisionEdgeKind.DependsOn, "needs", revision, ct);
        await service.RegisterTaskAsync("a", new DecisionWrite("remover", "s", "register-remover"), "Remover", cancellationToken: ct);
        await service.UnlinkAsync("a", new DecisionWrite("remover", "remover-session", "unlink"), edge.Value.EdgeId, "obsolete prerequisite", edge.ProjectRevision + 1, ct);
        SqliteConnection.ClearAllPools();

        var restarted = Service();
        var export = (await restarted.ExportAsync("a", cancellationToken: ct)).Value;
        export.SelectedTaskIds.ShouldBe(["remover"]);
        var removed = export.Edges.ShouldHaveSingleItem();
        removed.EdgeId.ShouldBe(edge.Value.EdgeId);
        removed.TaskId.ShouldBe("task");
        removed.RemovalTaskId.ShouldBe("remover");
        removed.RemovalSessionId.ShouldBe("remover-session");
        removed.RemovalReason.ShouldBe("obsolete prerequisite");
        removed.RemovedRevision.ShouldBe(edge.ProjectRevision + 2);
        export.Decisions.Single().Touches.ShouldHaveSingleItem().ShouldBe(new DecisionTouch("remover", "edge-removed", EdgeId: edge.Value.EdgeId));
        export.ReferencedTasks.ShouldHaveSingleItem().TaskId.ShouldBe("task");
        (await restarted.PreviewAsync("a", cancellationToken: ct)).Value.Total.Edges.ShouldBe(0);
    }

    [RetryFact]
    public async Task DecisionPreview_ManyEmptyTasks()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        const int count = 60;
        var title = string.Concat(Enumerable.Repeat("🐾", 90));
        for (var i = 0; i < count; i++)
        {
            var id = "empty-" + i.ToString(CultureInfo.InvariantCulture);
            await service.RegisterTaskAsync("a", new DecisionWrite(id, "s", "register-" + id), title, cancellationToken: ct);
        }

        var preview = (await service.PreviewAsync("a", new DecisionPreviewOptions(new DecisionTaskSelector(all: true), MaxChars: 1000), ct)).Value;
        preview.Text.EnumerateRunes().Count().ShouldBeLessThanOrEqualTo(1000);
        Should.NotThrow(() => StrictUtf8.GetByteCount(preview.Text));
        preview.Total.Tasks.ShouldBe(count);
        preview.Shown.Tasks.ShouldBeGreaterThan(0);
        preview.Omitted.Tasks.ShouldBe(count - preview.Shown.Tasks);
        preview.Omitted.Tasks.ShouldBeGreaterThan(0);
        preview.Text.Split('\n').Count(line => line.StartsWith("Task ", StringComparison.Ordinal)).ShouldBe(preview.Shown.Tasks);
        preview.Text.ShouldContain(FormattableString.Invariant($"Shown tasks {preview.Shown.Tasks}/{count}"));
        preview.Text.ShouldContain(FormattableString.Invariant($"omitted tasks {preview.Omitted.Tasks}"));
        preview.Text.ShouldContain("Task empty-59:");
    }

    private SqliteDecisionStore Store()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new SqliteDecisionStore(new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), new SharedProjectScope());
    }

    private sealed class InterleavedStore(IDecisionStore inner, Func<Task> afterRevision) : IDecisionStore
    {
        public Task<T> ReadAsync<T>(string projectId, Func<IDecisionReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(projectId, (reader, ct) => read(new InterleavedReader(reader, afterRevision), ct), cancellationToken);

        public Task<T> WriteAsync<T>(string projectId, Func<IDecisionWriter, CancellationToken, Task<T>> write, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(projectId, write, cancellationToken);
    }

    private sealed class InterleavedReader(IDecisionReader inner, Func<Task> afterRevision) : IDecisionReader
    {
        public async Task<long> RevisionAsync(CancellationToken cancellationToken)
        {
            var revision = await inner.RevisionAsync(cancellationToken);
            await afterRevision();
            return revision;
        }

        public Task<IReadOnlyList<DecisionTask>> TasksAsync(CancellationToken cancellationToken) => inner.TasksAsync(cancellationToken);
        public Task<DecisionRecord?> RecordAsync(string decisionId, CancellationToken cancellationToken) => inner.RecordAsync(decisionId, cancellationToken);
        public Task<IReadOnlyList<DecisionRecord>> RecordsAsync(CancellationToken cancellationToken) => inner.RecordsAsync(cancellationToken);
        public Task<IReadOnlyList<DecisionVersion>> VersionsAsync(string decisionId, CancellationToken cancellationToken) => inner.VersionsAsync(decisionId, cancellationToken);
        public Task<IReadOnlyList<DecisionVersion>> CurrentVersionsAsync(CancellationToken cancellationToken) => inner.CurrentVersionsAsync(cancellationToken);
        public Task<IReadOnlyList<DecisionVersion>> AllVersionsAsync(CancellationToken cancellationToken) => inner.AllVersionsAsync(cancellationToken);
        public Task<IReadOnlyList<DecisionSearchHit>> SearchAsync(string query, CancellationToken cancellationToken) => inner.SearchAsync(query, cancellationToken);
        public Task<DecisionCurrentPage> FindCurrentAsync(DecisionCurrentFilter filter, CancellationToken cancellationToken) => inner.FindCurrentAsync(filter, cancellationToken);
        public Task<IReadOnlyList<DecisionEdge>> EdgesAsync(CancellationToken cancellationToken) => inner.EdgesAsync(cancellationToken);
        public Task<DecisionReceipt?> ReceiptAsync(string requestId, CancellationToken cancellationToken) => inner.ReceiptAsync(requestId, cancellationToken);
        public Task<IReadOnlyList<DecisionImport>> ImportsAsync(CancellationToken cancellationToken) => inner.ImportsAsync(cancellationToken);
    }
}
