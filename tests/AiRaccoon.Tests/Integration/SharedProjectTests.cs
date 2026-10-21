using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using NSubstitute;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SharedProjectTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("shared-project");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public SharedProjectTests()
    {
        var options = TestData.CreateInfrastructureOptions(_root);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(), new FakeTimeProvider(),
            TestData.CreateEmbeddingService(), null, null, null, null, new StubCodeChunker(), null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_root);

    [RetryFact]
    public async Task SharedFile_IsPublishedToEveryProject_WithoutARepository()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("guide.md", "zephyrshared published guide");

        await _store.IngestFileAsync("shared", file, null, ct);

        Directory.Exists(Path.Combine(_root, ".git")).ShouldBeFalse();
        await using var connection = await _factory.OpenBankAsync(ct);
        (await connection.QueryAsync<string>("SELECT scope FROM entries WHERE path = @file", new { file }))
            .ShouldBe(["shared"]);
        (await SearchAsync("alpha", "zephyrshared")).ShouldHaveSingleItem();
        (await SearchAsync("beta", "zephyrshared")).ShouldHaveSingleItem();
        (await SearchAsync("shared", "zephyrshared")).ShouldHaveSingleItem();
        (await SearchAsync("alpha", "zephyrshared", SearchScope.Project)).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task LegacySharedOwner_IsRefusedWithoutPublishingPrivateRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.WriteAsync(new MemoryWriteRequest("alpha", "zephyrlegacy private"), ct);
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("UPDATE entries SET project_id = 'shared' WHERE project_id = 'alpha'");

        var ex = await Should.ThrowAsync<ArgumentException>(() => ((IProjectRegistry)_store).IsRegisteredAsync("shared", ct));

        ex.Message.ShouldContain("shared-project-conflict");
        (await SearchAsync("beta", "zephyrlegacy")).ShouldBeEmpty();
    }

    [RetryFact]
    public async Task SharedReplacement_RejectsForeignChunkAndRollsBackEarlierChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("collision.md", "zephyrold original document");
        await _store.ReplaceIfFileChangedAsync("shared", file, "old-fingerprint", ct);
        await File.WriteAllTextAsync(file, string.Join("\n\n", Enumerable.Range(0, 180)
            .Select(i => $"## Section {i}\nzephyrnew clause {i}: The document has a distinct statement about shared knowledge and ownership.")), ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("alpha"), IngestScopeKeys.Serialize([_root]), ct);
        await _store.IngestFileAsync("alpha", file, null, ct);
        await using var connection = await _factory.OpenBankAsync(ct);
        var count = await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM entries WHERE project_id = 'alpha'");
        count.ShouldBeGreaterThan(1);
        await connection.ExecuteAsync("UPDATE entries SET scope = 'shared' WHERE id = (SELECT max(id) FROM entries WHERE project_id = 'alpha')");
        var before = (await connection.QueryAsync<string>("SELECT hash FROM entries WHERE project_id = 'shared' ORDER BY hash")).ToArray();

        var ex = await Should.ThrowAsync<ArgumentException>(() => _store.ReplaceIfFileChangedAsync("shared", file, "new-fingerprint", ct));

        ex.Message.ShouldContain("shared-project-conflict");
        (await connection.QueryAsync<string>("SELECT hash FROM entries WHERE project_id = 'shared' ORDER BY hash")).ShouldBe(before);
        (await connection.ExecuteScalarAsync<string>("SELECT file_hash FROM watch_files WHERE project_id = 'shared' AND path = @file", new { file }))
            .ShouldBe("old-fingerprint");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM entries WHERE project_id = 'alpha' AND scope = 'shared'")).ShouldBe(1);
    }

    [RetryFact]
    public async Task LegacySharedWatch_IsNotActivatedByAnUpgrade()
    {
        var ct = TestContext.Current.CancellationToken;
        var watches = new WatchStore(_factory, TestData.SharedScope);
        await watches.AddWatchAsync("shared", _root, 0, 0, ct);

        var ex = await Should.ThrowAsync<ArgumentException>(() => ((IProjectRegistry)_store).IsRegisteredAsync("shared", ct));

        ex.Message.ShouldContain("shared-project-conflict");
        (await watches.ListWatchesAsync(ct)).ShouldHaveSingleItem();
    }

    [RetryFact]
    public async Task SharedReads_AggregatePromotionsAndDocuments_WithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("readme.md", "zephyraggregate watched document");
        await _store.IngestFileAsync("shared", file, null, ct);
        var note = await _store.WriteAsync(new MemoryWriteRequest("alpha", "zephyraggregate promoted note"), ct);
        await _store.ShareAsync("alpha", note.Hash, ct);

        (await SearchAsync("shared", "zephyraggregate")).Count.ShouldBe(2);
        (await SearchAsync("shared", "zephyraggregate", SearchScope.Shared)).Count.ShouldBe(2);
        var stats = await _store.GetStatsAsync("shared", ct);
        stats.EntryCount.ShouldBe(2);
        stats.Contexts.ShouldBe(["shared"]);
        (await _store.ListFilesAsync("shared", ct)).ShouldContain("readme.md");
    }

    [RetryFact]
    public async Task SharedProjectScopeAndWorkspaceSearch_AreExplicitlyUnsupported()
    {
        var ct = TestContext.Current.CancellationToken;
        var projectScope = await Should.ThrowAsync<ArgumentException>(() =>
            SearchAsync("shared", "example", SearchScope.Project));
        projectScope.Message.ShouldContain("shared-project-unsupported");

        var workspace = await Should.ThrowAsync<ArgumentException>(() => _store.SearchAsync(
            new SearchQuery("shared", "example", WorkspaceId: "scratch"), ct));
        workspace.Message.ShouldContain("shared-project-unsupported");
    }

    [RetryFact]
    public async Task SharedNotesAndCustomIngestContexts_AreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("label.md", "zephyrlabel private accident");
        await Should.ThrowAsync<ArgumentException>(() => _store.WriteAsync(new MemoryWriteRequest("shared", "a note"), ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.IngestFileAsync("shared", file, "private-label", ct));
    }

    [RetryFact]
    public async Task SharedDirectory_IndexesDocumentsAndSkipsCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await DocumentAsync("guide.md", "zephyrdirectory markdown");
        await DocumentAsync("facts.txt", "zephyrdirectory plain text");
        await DocumentAsync("Library.cs", "public class SharedAccident {} ");

        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("alpha"), IngestScopeKeys.Serialize([_root]), ct);
        await _store.IngestFileAsync("alpha", Path.Combine(_root, "Library.cs"), null, ct);
        await using (var positive = await _factory.OpenBankAsync(ct))
        {
            (await positive.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries WHERE project_id = 'alpha'")).ShouldBeGreaterThan(0);
        }

        await _store.IngestDirectoryAsync("shared", _root, null, ct);

        (await SearchAsync("beta", "zephyrdirectory")).Count.ShouldBe(2);
        await using var connection = await _factory.OpenBankAsync(ct);
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM code_entries WHERE project_id = 'shared'")).ShouldBe(0);
    }

    [RetryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLifecycle_PreservesPrivateCopiesAndPromotedContributions(bool watchReplacement)
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("lifecycle.md", "zephyrlifecycle original");
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("alpha"), IngestScopeKeys.Serialize([_root]), ct);
        await _store.IngestFileAsync("alpha", file, null, ct);
        var privateRow = (await SearchAsync("alpha", "zephyrlifecycle", SearchScope.Project)).ShouldHaveSingleItem();
        await _store.ShareAsync("alpha", privateRow.Hash, ct);
        await IngestAsync();
        await File.WriteAllTextAsync(file, "zephyrrevised replacement", ct);
        await IngestAsync();

        var current = (await SearchAsync("beta", "zephyrrevised")).ShouldHaveSingleItem();
        current.SourceFile.ShouldBe(file);
        (await SearchAsync("beta", "zephyrlifecycle")).ShouldHaveSingleItem();
        await _store.DeleteSourcePathAsync("shared", file, ct);
        (await SearchAsync("beta", "zephyrrevised")).ShouldBeEmpty();
        (await SearchAsync("alpha", "zephyrlifecycle", SearchScope.Project)).ShouldHaveSingleItem();
        (await SearchAsync("beta", "zephyrlifecycle")).ShouldHaveSingleItem();
        await IngestAsync();
        (await SearchAsync("beta", "zephyrrevised")).ShouldHaveSingleItem();

        async Task IngestAsync()
        {
            if (watchReplacement)
            {
                var content = await File.ReadAllTextAsync(file, ct);
                await _store.ReplaceIfFileChangedAsync("shared", file, WatchDigestExecutor.ComputeHash(file, content), ct);
            }
            else
            {
                await _store.IngestFileAsync("shared", file, null, ct);
            }
        }
    }

    [RetryFact]
    public async Task SharedBackend_RefusesWorkspaceAndContentWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await Should.ThrowAsync<ArgumentException>(() => new SqliteWorkspaceStore(_factory, TestData.SharedScope)
            .BeginAsync(new Workspace("scratch", "shared"), DateTimeOffset.UnixEpoch, ct));
        await Should.ThrowAsync<ArgumentException>(() => _store.AddContentAsync("shared", "manual.md", "example", "shared", cancellationToken: ct));
    }

    [RetryTheory]
    [InlineData("fold-from")]
    [InlineData("fold-to")]
    [InlineData("drop")]
    [InlineData("retire")]
    public async Task Shared_RejectsManuallyConstructedRepairPlans(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        var plan = new ProjectIdsFoldPlan(
            action == "fold-from" ? [new ProjectIdFold("shared", "alpha")]
                : action == "fold-to" ? [new ProjectIdFold("alpha", "shared")] : [],
            action == "drop" ? ["shared"] : [], action == "retire" ? ["shared"] : [], []);

        await Should.ThrowAsync<ArgumentException>(() => new ProjectIdsRepair(TimeProvider.System, TestData.SharedScope).ApplyAsync(connection, plan, ct));
    }

    [RetryTheory]
    [InlineData(WatchEventKind.Deleted)]
    [InlineData(WatchEventKind.Renamed)]
    [InlineData(WatchEventKind.Changed)]
    public async Task LegacySharedWatch_RefusesEveryDigestBeforeChangingPrivateData(WatchEventKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("private.md", "zephyrprivatemirror legacy content");
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("alpha"), IngestScopeKeys.Serialize([_root]), ct);
        await _store.ReplaceIfFileChangedAsync("alpha", file, "private-fingerprint", ct);
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync("UPDATE entries SET project_id = 'shared'; UPDATE watch_files SET project_id = 'shared'");
        var watches = new WatchStore(_factory, TestData.SharedScope);
        await watches.AddWatchAsync("shared", _root, 0, 0, ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "ai-raccoon.ignore"), "private.md", ct);
        var executor = new WatchDigestExecutor(_store, watches, new FakeTimeProvider(),
            new IgnoreRulesProvider(), new Lazy<IWatchScanInitiator>(() => Substitute.For<IWatchScanInitiator>()),
            TestData.NewEmbedDrainPump(), new SqliteProjectIdsMigrationGate(_factory),
            TestData.RealFileTypeMatcher(), new CodeFileTypeMatcher(), TestData.SharedScope);

        await Should.ThrowAsync<ArgumentException>(() => executor.DigestAsync("shared", _root,
            kind == WatchEventKind.Renamed ? Path.Combine(_root, "new.md") : file,
            kind, kind == WatchEventKind.Renamed ? file : null, ct));

        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM entries WHERE project_id = 'shared' AND scope = 'project'")).ShouldBeGreaterThan(0);
        (await connection.ExecuteScalarAsync<string>("SELECT file_hash FROM watch_files WHERE project_id = 'shared'")).ShouldBe("private-fingerprint");
        (await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM sync_tombstones")).ShouldBe(0);
    }

    [RetryFact]
    public async Task SharedDelete_IsRefused_AndLeavesTheDocumentAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = await DocumentAsync("gated.md", "zephyrdelete gated document");
        await _store.IngestFileAsync("shared", file, null, ct);
        // The delete is a destructive operation, so the access mode has to allow it: what is
        // under test is the reserved target's own refusal, not the access tier's.
        await _store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, "full", ct);

        await using var connection = await _factory.OpenBankAsync(ct);
        var hash = await connection.ExecuteScalarAsync<string>(
            "SELECT hash FROM entries WHERE project_id = 'shared' LIMIT 1");
        hash.ShouldNotBeNull("the ingested document has a chunk to delete");
        var before = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM entries WHERE project_id = 'shared'");

        var outcome = await Record.ExceptionAsync(() => MemoryTool().Delete("shared", hash!, ct));

        (outcome?.Message ?? "no refusal").ShouldContain("shared-project-unsupported");
        var after = await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM entries WHERE project_id = 'shared'");
        after.ShouldBe(before, "the refused delete left the document's rows alone");
    }

    /// <summary>The real tool over the real composition root: the gate, the scope check and the store.</summary>
    private MemoryTools MemoryTool()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterMemoryServices(new InfrastructureOptions
        {
            DataRoot = _root,
            Scope = InstallScope.User
        });
        using var provider = services.BuildServiceProvider();
        return (MemoryTools)ActivatorUtilities.CreateInstance(provider, typeof(MemoryTools));
    }

    private async Task<string> DocumentAsync(string name, string text)
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SetSettingAsync(IngestScopeKeys.ScopeProject("shared"), IngestScopeKeys.Serialize([_root]), ct);
        var file = Path.Combine(_root, name);
        await File.WriteAllTextAsync(file, text, ct);
        return file;
    }

    private async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string project, string query,
        SearchScope scope = SearchScope.All) =>
        (await _store.SearchAsync(new SearchQuery(project, query, Scope: scope, MinRelativeScore: 0),
            TestContext.Current.CancellationToken)).Results;
}
