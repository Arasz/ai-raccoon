using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Code;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Mcp;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedEntryLifecycleTests : IAsyncLifetime
{
    private const string ProjectId = "acme";
    private const string QueryTerm = "TypedLifecycleWombatCode";
    private readonly string _dataRoot = TestData.CreateTempRoot("typed-entry-lifecycle");
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private MemoryTools _memoryTools = null!;
    private EntryTools _entryTools = null!;
    private SweepTools _sweepTools = null!;

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var embeddings = new CountingEmbeddingService();
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)), embeddings,
            null, null, null, null, null, null, null);
        var settings = new SqliteSettingsStore(_factory);
        var codeSearch = new SqliteCodeSearchService(_factory, new FakeCodeEmbedder());
        var guard = new MemoryAccessGuard(_store);
        var gate = new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore(),
            new AllowingRegistrationGuard(), TestData.ScopeCheck);
        var quality = new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance);
        _memoryTools = new MemoryTools(_store, gate,
            new SearchDispatcher(_store, codeSearch, quality, TestData.SharedScope),
            new QueryGuardService(settings), new MemoryWriteService(_store, new FakePromotionQueue(), TestData.SharedScope),
            new NoOpMeasurementRecorder(), settings, NullLogger<MemoryTools>.Instance, embeddings);
        _entryTools = new EntryTools(_store, codeSearch, gate);
        _sweepTools = new SweepTools(new SweepService(_store, TimeProvider.System),
            new ForgettingPolicyService(_store, guard), gate);
        await using var warm = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    [RetryFact]
    public async Task DefaultQueryAndTypedLookup_KeepMemoryFileAndCodeDistinct()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(ProjectId), "full", ct);
        var memory = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, $"{QueryTerm} agent note"), ct);
        var transcript = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, $"{QueryTerm} session transcript",
            SourceFile: "hermes/session-lifecycle"), ct);
        var citingMemory = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, $"{QueryTerm} note cites a document",
            SourceFile: "docs/referenced.md"), ct);
        var otherProject = await _store.WriteAsync(new MemoryWriteRequest("other", $"{QueryTerm} outside the requested project"), ct);
        await SeedFileAsync("file-hash", $"{QueryTerm} mirrored file");
        await SeedCodeAsync("code-hash", $"sealed class {QueryTerm} {{ }}");

        var all = await _memoryTools.Search(ProjectId, QueryTerm, "lifecycle-session", ftsWeight: 1, vectorWeight: 0,
            minRelativeScore: 0, cancellationToken: ct);
        var memoryAndFiles = await _memoryTools.Search(ProjectId, QueryTerm, "lifecycle-session",
            entryType: "memory,file", ftsWeight: 1, vectorWeight: 0, minRelativeScore: 0, cancellationToken: ct);

        all.Data!.Results.Select(hit => hit.Hash).ShouldContain(memory.Hash);
        all.Data.Results.Select(hit => hit.Hash).ShouldContain(transcript.Hash);
        all.Data.Results.Select(hit => hit.Hash).ShouldContain(citingMemory.Hash);
        all.Data.Results.Select(hit => hit.Hash).ShouldContain("file-hash");
        all.Data.Results.Select(hit => hit.Hash).ShouldNotContain(otherProject.Hash);
        all.Data.Code!.Select(hit => hit.Hash).ShouldContain("code-hash");
        memoryAndFiles.Data!.Code.ShouldBeNull();
        memoryAndFiles.Data.Results.Select(hit => hit.Hash).ShouldContain("file-hash");
        memoryAndFiles.Data.Results.Select(hit => hit.Hash).ShouldContain(memory.Hash);

        var file = (await _entryTools.GetEntry(ProjectId, "file-hash", "file", ct)).Data!;
        var transcriptEntry = (await _entryTools.GetEntry(ProjectId, transcript.Hash, "memory", ct)).Data!;
        var code = (await _entryTools.GetEntry(ProjectId, "code-hash", "code", ct)).Data!;
        file.EntryKind.ShouldBe("file");
        transcriptEntry.EntryKind.ShouldBe("memory");
        code.EntryKind.ShouldBe("code");

        await using var connection = await _factory.OpenBankAsync(ct);
        var quality = await connection.QuerySingleAsync<QualityRow>(new CommandDefinition(
            "SELECT kind AS Kind, kind_mask AS KindMask FROM search_quality WHERE session_id='lifecycle-session' AND kind_mask=7 ORDER BY rowid LIMIT 1",
            cancellationToken: ct));
        quality.Kind.ShouldBe("both");
        quality.KindMask.ShouldBe((long)EntryTypeParameter.Default);
        var memoryFileQuality = await connection.QuerySingleAsync<QualityRow>(new CommandDefinition(
            "SELECT kind AS Kind, kind_mask AS KindMask FROM search_quality WHERE session_id='lifecycle-session' AND kind_mask=5 ORDER BY rowid LIMIT 1",
            cancellationToken: ct));
        memoryFileQuality.Kind.ShouldBe("memory");
    }

    [RetryFact]
    public async Task FileTtl_RefusesSetButAllowsClear_WhileMemoryCanChange()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(ProjectId), "full", ct);
        var memory = await _store.WriteAsync(new MemoryWriteRequest(ProjectId, "a durable lifecycle note"), ct);
        await SeedFileAsync("file-ttl-hash", "mirrored document");
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET ttl_days=19 WHERE hash='file-ttl-hash'", cancellationToken: ct));
        }

        (await _sweepTools.SetTtl(ProjectId, memory.Hash, 7, ct)).Data!.TtlDays.ShouldBe(7);
        await Should.ThrowAsync<UnknownHashException>(() => _sweepTools.SetTtl(ProjectId, "file-ttl-hash", 3, ct));
        (await _store.GetMetadataAsync(ProjectId, "file-ttl-hash", ct))!.TtlDays.ShouldBe(19);
        (await _sweepTools.SetTtl(ProjectId, "file-ttl-hash", null, ct)).Data!.TtlDays.ShouldBeNull();
        (await _store.GetMetadataAsync(ProjectId, "file-ttl-hash", ct))!.TtlDays.ShouldBeNull();
    }

    private async Task SeedFileAsync(string hash, string value)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO entries(hash,path,value,source_file,entry_type,scope,project_id,created_at,updated_at)
            VALUES(@hash,@path,@value,@path,'file','project',@projectId,1,1)
            """, new { hash, path = $"docs/{hash}.md", value, projectId = ProjectId },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    private async Task SeedCodeAsync(string hash, string value)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO code_entries(hash,path,value,source_file,line_start,line_end,project_id,created_at,updated_at)
            VALUES(@hash,'src/Lifecycle.cs',@value,'src/Lifecycle.cs',1,1,@projectId,1,1)
            """, new { hash, value, projectId = ProjectId }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed record QualityRow(string Kind, long KindMask);
}
