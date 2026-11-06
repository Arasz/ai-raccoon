using System.Reflection;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Decisions;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Decisions;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Decisions;
using AiRaccoon.Tests.Unit.Decisions;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Decisions;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class DecisionImportTests : IAsyncLifetime
{
    private const string Project = "a";
    private const string Db05 = "db05c33e-6e9e-458c-b6be-d9f3be2c51eb";
    private static readonly DateTimeOffset Clock = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = TestData.CreateTempRoot("decisions-import");
    private readonly byte[] _source = SemanticaParsingTests.Fixture("export-graph-json-0.7.0.json");
    private readonly byte[] _mapping = SemanticaParsingTests.Fixture("export-graph-json-0.7.0.mapping.json");

    private SqliteConnectionFactory Factory()
    {
        var options = new InfrastructureOptions { DataRoot = _root, Rid = "osx-arm64", Scope = InstallScope.User };
        return new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    private DecisionService Service(Func<IDecisionStore, IDecisionStore>? wrap = null)
    {
        IDecisionStore store = new SqliteDecisionStore(Factory(), new SharedProjectScope());
        return new DecisionService(wrap?.Invoke(store) ?? store, new FakeTimeProvider(Clock));
    }

    private async Task<DecisionService> SeededAsync(Func<IDecisionStore, IDecisionStore>? wrap = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service(wrap);
        await service.RegisterTaskAsync(Project, new DecisionWrite("semantica-legacy", "s", "register-legacy"), "Legacy", cancellationToken: ct);
        await service.RegisterTaskAsync(Project, new DecisionWrite("storage-task", "s", "register-storage"), "Storage", cancellationToken: ct);
        return service;
    }

    private static DecisionImportCommand Command(byte[] source, byte[] mapping, long revision, string requestId = "import-1") =>
        new("session", requestId, source, mapping, SemanticaImportPlan.Digest(source), SemanticaImportPlan.Digest(mapping), revision);

    private async Task<string> DumpAsync()
    {
        await using var connection = await Factory().OpenBankAsync(TestContext.Current.CancellationToken);
        return await DecisionGraphFixture.DumpGraphAsync(connection, Project);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = await Factory().OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<T>(sql) ?? throw new InvalidOperationException(sql);
    }

    private static byte[] Edited(byte[] source, string from, string to)
    {
        var text = Encoding.UTF8.GetString(source);
        text.ShouldContain(from);
        return Encoding.UTF8.GetBytes(text.Replace(from, to));
    }

    [RetryFact]
    public async Task DecisionImport_DryRunReplayAndUnknownData()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = await SeededAsync();
        var before = await DumpAsync();

        var preview = await service.PreviewSemanticaImportAsync(Project, _source, _mapping, ct);

        preview.Value.CanApply.ShouldBeTrue();
        preview.Value.Counts.ShouldBe(new DecisionImportCounts(9, 13, 4, 3, 15, 0, 0));
        preview.Value.SourceDigest.ShouldBe("290A36C00C2DDE2CBA4C32243E71F5244D9FEA0D8F8F17B5560AD475B74F5A11");
        preview.ProjectRevision.ShouldBe(2);
        (await DumpAsync()).ShouldBe(before);

        var applied = await service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, preview.ProjectRevision), ct);
        applied.ProjectRevision.ShouldBe(3);
        applied.Replayed.ShouldBeFalse();
        applied.Value.Counts.ShouldBe(preview.Value.Counts);
        var afterApply = await DumpAsync();
        afterApply.ShouldNotBe(before);

        var replay = await Service().ApplySemanticaImportAsync(Project, Command(_source, _mapping, preview.ProjectRevision), ct);
        replay.Replayed.ShouldBeTrue();
        replay.ProjectRevision.ShouldBe(3);
        (await DumpAsync()).ShouldBe(afterApply);

        var export = (await service.ExportAsync(Project, new DecisionTaskSelector(all: true), ct)).Value;
        export.ImportRecords.Count.ShouldBe(22);
        var source = SemanticaExport.Parse(_source);
        export.ImportRecords.Single(row => row.ExternalId == "person-1").RawPayload.ShouldBe(source.Entities.Single(entity => entity.Id == "person-1").RawText);
        export.ImportRecords.Single(row => row.ExternalId == "bb8831a1-e0fa-55c8-a585-f17a28eb988d").RawPayload
            .ShouldBe(source.Relationships.Single(link => link.Type == "CAUSED").RawText);
        export.ImportRecords.Count(row => row.DecisionId is not null).ShouldBe(4);
        export.Edges.Count.ShouldBe(3);
        export.Edges.ShouldAllBe(edge => edge.Kind != DecisionEdgeKind.Supersedes);
        export.Decisions.Count.ShouldBe(4);
        export.Decisions.ShouldAllBe(decision => decision.Versions.Single().RecordedAt == Clock && decision.Record.CreatedAt == Clock);
        export.Decisions.Single(decision => decision.Versions.Single().Content.Title.StartsWith("Zapisa")).Versions.Single().Status.ShouldBe(DecisionStatus.Rejected);
        export.Decisions.Single(decision => decision.Versions.Single().Content.Title.StartsWith("Zapisa")).Versions.Single().TaskId.ShouldBe("storage-task");
        export.Decisions.ShouldAllBe(decision => decision.Versions.Single().ChangeReason == null && decision.Versions.Single().ProjectRevision == 3);
        export.Edges.ShouldAllBe(edge => edge.CreatedAt == Clock && edge.CreatedRevision == 3);
    }

    [RetryFact]
    public async Task Apply_NewRequestIdOnImportedData_WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = await SeededAsync();
        await service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 2), ct);
        var after = await DumpAsync();

        var again = await service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 3, "import-2"), ct);

        again.Replayed.ShouldBeTrue();
        again.ProjectRevision.ShouldBe(3);
        again.Value.Counts.ShouldBe(new DecisionImportCounts(9, 13, 0, 0, 0, 22, 0));
        var stale = await Should.ThrowAsync<DecisionRevisionConflictException>(() => service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 2, "import-3"), ct));
        stale.ShouldNotBeNull();
        (await ScalarAsync<long>("SELECT count(*) FROM decision_requests WHERE request_id='import-3'")).ShouldBe(0);
        (await ScalarAsync<long>("SELECT count(*) FROM decision_imports")).ShouldBe(22);
        (await ScalarAsync<long>("SELECT count(*) FROM decisions")).ShouldBe(4);
        (await ScalarAsync<long>("SELECT count(*) FROM decision_requests WHERE request_id='import-2'")).ShouldBe(1);
        after.ShouldNotBeEmpty();
    }

    [RetryFact]
    public async Task Apply_FailureMidWrite_LeavesEveryTableUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var failing = await SeededAsync(store => new FailingStore(store, failOnImport: 6));
        var before = await DumpAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => failing.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 2), ct));

        (await DumpAsync()).ShouldBe(before);
        (await ScalarAsync<long>("SELECT count(*) FROM decision_imports")).ShouldBe(0);
        (await ScalarAsync<long>("SELECT count(*) FROM decision_edges")).ShouldBe(0);
    }

    [RetryFact]
    public async Task Apply_StaleRevision_DigestMismatch_ChangedSource_ChangedMapping_Refuse()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = await SeededAsync();
        var stale = await Should.ThrowAsync<DecisionRevisionConflictException>(() => service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 1), ct));
        stale.CurrentRevision.ShouldBe(2);
        var wrongSource = Command(_source, _mapping, 2) with { SourceDigest = SemanticaImportPlan.Digest(_mapping) };
        (await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, wrongSource, ct))).Kind.ShouldBe("decision-conflict");
        var wrongMapping = Command(_source, _mapping, 2) with { MappingDigest = SemanticaImportPlan.Digest(_source) };
        (await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, wrongMapping, ct))).Kind.ShouldBe("decision-conflict");
        var lower = Command(_source, _mapping, 2) with { SourceDigest = SemanticaImportPlan.Digest(_source).ToLowerInvariant(), MappingDigest = SemanticaImportPlan.Digest(_mapping).ToLowerInvariant() };
        var lowerResult = await service.ApplySemanticaImportAsync(Project, lower, ct);
        lowerResult.Replayed.ShouldBeFalse();

        var edited = Edited(_source, "Latest task is what a reader wants first", "Latest task is what a reader wants first, edited");
        var changedSource = await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, Command(edited, _mapping, 3, "r2"), ct));
        changedSource.Kind.ShouldBe("decision-import-blocked");
        var remapped = Edited(_mapping, "\"status\": \"accepted\"", "\"status\": \"deprecated\"");
        var changedMapping = await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, Command(_source, remapped, 3, "r3"), ct));
        changedMapping.Kind.ShouldBe("decision-import-blocked");

        var preview = await service.PreviewSemanticaImportAsync(Project, edited, _mapping, ct);
        preview.Value.Blockers.Select(blocker => blocker.Code).ShouldBe(["changed-source"]);
        (await service.PreviewSemanticaImportAsync(Project, _source, remapped, ct)).Value.Blockers.Select(blocker => blocker.Code).Distinct().ShouldBe(["changed-mapping"]);
    }

    [RetryFact]
    public async Task Apply_SameRequestIdWithADifferentFile_ConflictsOnTheRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = await SeededAsync();
        await service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 2), ct);
        var other = Edited(_source, "Latest task is what a reader wants first", "Other text");

        var remapped = Edited(_mapping, "\"status\": \"proposed\"", "\"status\": \"accepted\"");

        var changedSource = await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, Command(other, _mapping, 2), ct));
        var changedMapping = await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, Command(_source, remapped, 2), ct));

        changedSource.Kind.ShouldBe("decision-request-conflict");
        changedMapping.Kind.ShouldBe("decision-request-conflict");
    }

    [RetryFact]
    public async Task Apply_NewerExport_AddsOnlyNewRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = await SeededAsync();
        await service.ApplySemanticaImportAsync(Project, Command(_source, _mapping, 2), ct);
        var newer = NewerExport();

        var preview = await service.PreviewSemanticaImportAsync(Project, newer.Source, newer.Mapping, ct);
        preview.Value.Blockers.ShouldBeEmpty();
        preview.Value.Counts.Decisions.ShouldBe(1);
        preview.Value.Counts.Edges.ShouldBe(1);
        preview.Value.Counts.AlreadyImported.ShouldBe(22);
        var applied = await service.ApplySemanticaImportAsync(Project, Command(newer.Source, newer.Mapping, preview.ProjectRevision, "import-newer"), ct);

        applied.ProjectRevision.ShouldBe(4);
        (await ScalarAsync<long>("SELECT count(*) FROM decisions")).ShouldBe(5);
        (await ScalarAsync<long>("SELECT count(*) FROM decision_edges")).ShouldBe(4);
        (await ScalarAsync<long>("SELECT count(*) FROM decision_imports")).ShouldBe(24);
    }

    private (byte[] Source, byte[] Mapping) NewerExport()
    {
        using var document = JsonDocument.Parse(_source);
        var inner = JsonDocument.Parse(document.RootElement.GetProperty("data").GetString()!).RootElement;
        var entities = inner.GetProperty("entities").EnumerateArray().Select(entity => entity.GetRawText()).ToList();
        var links = inner.GetProperty("relationships").EnumerateArray().Select(link => link.GetRawText()).ToList();
        entities.Add("""{"id":"newest","text":"Newest","type":"decision","properties":{"category":"c","scenario":"s","outcome":"o","reasoning":"r"}}""");
        links.Add($$"""{"id":"newest-link","source_id":"newest","target_id":"{{Db05}}","type":"supports"}""");
        var data = "{\"entities\":[" + string.Join(",", entities) + "],\"relationships\":[" + string.Join(",", links)
            + "],\"statistics\":{\"entity_count\":" + entities.Count + ",\"relationship_count\":" + links.Count + "}}";
        var source = Encoding.UTF8.GetBytes("{\"format\":\"json\",\"data\":" + JsonSerializer.Serialize(data) + "}");
        var map = System.Text.Json.Nodes.JsonNode.Parse(_mapping)!.AsObject();
        map["records"]!["newest"] = System.Text.Json.Nodes.JsonNode.Parse("""{"externalId":"newest","status":"proposed"}""");
        map["records"]!["newest-link"] = System.Text.Json.Nodes.JsonNode.Parse("""{"externalId":"newest-link"}""");
        return (source, Encoding.UTF8.GetBytes(map.ToJsonString()));
    }

    [RetryFact]
    public async Task Blocked_OversizedAndMalformedInput_RefusesBeforeWriting()
    {
        var ct = TestContext.Current.CancellationToken;
        var fresh = Service();
        var before = await DumpAsync();

        var empty = SemanticaParsingTests.Envelope("""{"entities":[],"relationships":[],"statistics":{"entity_count":0,"relationship_count":0}}""");
        var blocked = await Should.ThrowAsync<DecisionException>(() => fresh.ApplySemanticaImportAsync(Project, Command(empty, _mapping, 0), ct));
        blocked.Kind.ShouldBe("decision-import-blocked");
        var report = (await fresh.PreviewSemanticaImportAsync(Project, empty, _mapping, ct)).Value;
        report.Blockers.Select(blocker => blocker.Code).ShouldContain("unknown-task");

        var big = new byte[DecisionImportLimits.MaxBytes + 1];
        (await Should.ThrowAsync<DecisionException>(() => fresh.PreviewSemanticaImportAsync(Project, big, _mapping, ct))).Kind.ShouldBe("decision-import-too-large");
        (await Should.ThrowAsync<DecisionException>(() => fresh.PreviewSemanticaImportAsync(Project, _source, big, ct))).Kind.ShouldBe("decision-import-too-large");
        (await Should.ThrowAsync<DecisionException>(() => fresh.ApplySemanticaImportAsync(Project, Command(big, _mapping, 0), ct))).Kind.ShouldBe("decision-import-too-large");
        await Should.ThrowAsync<ArgumentNullException>(() => fresh.PreviewSemanticaImportAsync(Project, null!, _mapping, ct));
        await Should.ThrowAsync<ArgumentNullException>(() => fresh.PreviewSemanticaImportAsync(Project, _source, null!, ct));
        await Should.ThrowAsync<ArgumentNullException>(() => fresh.ApplySemanticaImportAsync(Project, new DecisionImportCommand("session", "r", null!, _mapping, "A", "B", 0), ct));
        (await Should.ThrowAsync<ArgumentException>(() => fresh.PreviewSemanticaImportAsync(Project, Encoding.UTF8.GetBytes("nope"), _mapping, ct))).ParamName.ShouldBe("source");
        (await Should.ThrowAsync<ArgumentException>(() => fresh.PreviewSemanticaImportAsync(Project, _source, Encoding.UTF8.GetBytes("nope"), ct))).ParamName.ShouldBe("mapping");
        (await DumpAsync()).ShouldBe(before);
    }

    [RetryFact]
    public async Task Apply_BlockedByAMissingStatus_WritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = await SeededAsync();
        var before = await DumpAsync();
        var noStatus = Edited(_mapping, "\"status\": \"proposed\"", "\"status\": \"nonsense\"");

        var blocked = await Should.ThrowAsync<DecisionException>(() => service.ApplySemanticaImportAsync(Project, Command(_source, noStatus, 2), ct));

        blocked.Kind.ShouldBe("decision-import-blocked");
        blocked.Message.ShouldContain("1 blocker");
        (await DumpAsync()).ShouldBe(before);
    }

    public async ValueTask InitializeAsync()
    {
        await using var connection = await Factory().OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT INTO projects(id,name,created_at) VALUES('a','A',1)");
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_root);
        return ValueTask.CompletedTask;
    }

    private sealed class FailingStore(IDecisionStore inner, int failOnImport) : IDecisionStore
    {
        public Task<T> ReadAsync<T>(string projectId, Func<IDecisionReader, CancellationToken, Task<T>> read, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(projectId, read, cancellationToken);

        public Task<T> WriteAsync<T>(string projectId, Func<IDecisionWriter, CancellationToken, Task<T>> write, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(projectId, (writer, token) => write(Counting.Wrap(writer, failOnImport), token), cancellationToken);
    }

    private class Counting : DispatchProxy
    {
        private IDecisionWriter _inner = null!;
        private int _remaining;

        public static IDecisionWriter Wrap(IDecisionWriter inner, int failOnImport)
        {
            var proxy = Create<IDecisionWriter, Counting>();
            var counting = (Counting)(object)proxy;
            counting._inner = inner;
            counting._remaining = failOnImport;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IDecisionWriter.AddImportAsync) && --_remaining == 0)
            {
                throw new InvalidOperationException("injected failure on the Nth import row");
            }

            return targetMethod.Invoke(_inner, args);
        }
    }
}
