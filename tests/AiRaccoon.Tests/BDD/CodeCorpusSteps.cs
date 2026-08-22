using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Reqnroll;
using Shouldly;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Bindings for docs/features/code-corpus/code-corpus.feature; production API surface is
///     documented in docs/work/2026-08-21-code-search-implementation-plan.md.
/// </summary>
[Binding]
public sealed class CodeCorpusSteps(ScenarioContext scenarioContext)
{
    private const string DefaultProject = "acme";
    private const string SyncObjectKey = "bdd-code-corpus-sync";

    /// <summary>Default query term shared by memory entry and code chunk fixtures so one search hits both.</summary>
    private string _searchQuery = "wombatzephyrshared";

    private CodeCorpusFeatureContext? _ctx;
    private readonly ScenarioContext _scenarioContext = scenarioContext;

    private Exception? _lastError;
    private ApiEnvelope<MemoryTools.SearchResultList>? _lastSearchEnvelope;
    private JsonObject? _lastSearchJson;
    private ApiEnvelope<CodeTools.CodeGetResult>? _lastCodeGetEnvelope;
    private string? _lastFilePath;
    private string? _manifestDir;
    private string? _rescanProbePath;
    private FakeCloudStore? _cloud;
    private string? _pushedSnapshotPath;

    private CodeCorpusFeatureContext Ctx => _ctx ??= _scenarioContext.ScenarioContainer.Resolve<CodeCorpusFeatureContext>();

    // ── Helpers ──

    private async Task SeedCodeEntryAsync(string hash, string path, string value, int lineStart, int lineEnd,
        string? projectId = null)
    {
        await using var connection = await Ctx.OpenBankAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@hash, @path, @value, @path, @lineStart, @lineEnd, @projectId, 1, 1)
            """,
            new { hash, path, value, lineStart, lineEnd, projectId = projectId ?? DefaultProject }));
    }

    private async Task SeedEmbeddedCodeEntryAsync(string hash, string path, string value, int lineStart, int lineEnd)
    {
        await SeedCodeEntryAsync(hash, path, value, lineStart, lineEnd);
        await using var connection = await Ctx.OpenBankAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE hash = @hash",
            new { hash, embedding = EmbeddingBlob.ToBytes(new float[768]) }));
    }

    private async Task<int> CountCodeEntriesForPathAsync(string path)
    {
        await using var connection = await Ctx.OpenBankAsync();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM code_entries WHERE path = @path",
            new { path = IngestPath.Normalize(path) }));
    }

    private async Task<int> CountMemoryEntriesForPathAsync(string path)
    {
        await using var connection = await Ctx.OpenBankAsync();
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE source_file = @path",
            new { path = IngestPath.Normalize(path) }));
    }

    private async Task RunSearchAsync(string projectId, string query, string? kind = null, string? scope = null)
    {
        _lastError = null;
        try
        {
            _lastSearchEnvelope = kind is null
                ? await Ctx.MemoryTools.Search(projectId, query)
                : await Ctx.MemoryTools.Search(projectId, query, scope: scope ?? "all", kind: kind);
            _lastSearchJson = JsonSerializer.SerializeToNode(_lastSearchEnvelope, McpJsonUtilities.DefaultOptions)!
                .AsObject()["data"]!.AsObject();
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    private async Task RunCodeGetAsync(string projectId, string hash)
    {
        _lastError = null;
        try
        {
            _lastCodeGetEnvelope = await Ctx.CodeTools.CodeGet(projectId, hash);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    private async Task RunModelSetCodeLocalAsync()
    {
        _lastError = null;
        try
        {
            await CliRun.RunAsync(
                ["model", "set", "code", "local", _manifestDir!],
                (parsed, streams, cancellationToken) =>
                    new SettingsCommands().ModelSetCodeLocalAsync(parsed.ParsedCliArgs, Ctx.CodeEngineStore, streams, cancellationToken));
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    private void SeedManifestDirectory(string dir, int dimensions)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var fixtureName = dimensions == CodeCorpusSchema.EmbeddingDimensions
            ? "code-daemon-embed-v1.json"
            : "code-daemon-embed-v1-non768.json";
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Resources", "ManifestFixtures", fixtureName);
        File.Copy(fixturePath, Path.Combine(dir, EmbeddingManifest.FileName));
    }

    private static async Task<SqliteConnection> OpenRawAsync(string path, bool readOnly, bool runSchema, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={path}{(readOnly ? ";Mode=ReadOnly" : string.Empty)}");
        await connection.OpenAsync(cancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        if (runSchema)
        {
            await MemorySchema.EnsureAsync(connection, cancellationToken);
        }

        return connection;
    }

    private async Task RunSyncPushAsync()
    {
        _cloud ??= new FakeCloudStore();
        var service = new SyncService(_cloud,
            ct => OpenRawAsync(Ctx.Factory.BankPath, readOnly: false, runSchema: true, ct),
            (path, ct) => OpenRawAsync(path, readOnly: false, runSchema: false, ct),
            (path, ct) => OpenRawAsync(path, readOnly: true, runSchema: false, ct),
            Ctx.TimeProvider, NullLogger<SyncService>.Instance);
        await service.MemorySyncAsync(DefaultProject, SyncObjectKey, CancellationToken.None);
        var pulled = await _cloud.PullAsync(SyncObjectKey, CancellationToken.None);
        pulled.ShouldNotBeNull("the sync push produced nothing to pull back");
        _pushedSnapshotPath = Path.Combine(Ctx.DataRoot, $"pulled-{Guid.NewGuid():N}.db");
        await File.WriteAllBytesAsync(_pushedSnapshotPath, pulled!.Data);
    }

    private async Task SeedRemoteMemoryOnlySnapshotAsync(string hash, string content)
    {
        _cloud = new FakeCloudStore();
        var remoteSeedPath = Path.Combine(Ctx.DataRoot, $"remote-seed-{Guid.NewGuid():N}.db");
        await using (var remoteConnection = await OpenRawAsync(remoteSeedPath, readOnly: false, runSchema: true, CancellationToken.None))
        {
            await remoteConnection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, 'remote.md', @content, 'project', @projectId, 1, 1)
                """,
                new { hash, content, projectId = DefaultProject }));
        }

        _cloud.Set(SyncObjectKey, await File.ReadAllBytesAsync(remoteSeedPath));
    }

    private static async Task<List<string>> TableNamesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        await connection.OpenAsync();
        return (await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'")).ToList();
    }

    // ── Rule: memory_search's kind parameter selects and shapes the response envelope ──

    [Given("^a project with memory entries and code chunks$")]
    public async Task GivenProjectWithMemoryAndCode()
    {
        await Ctx.MemoryTools.Write(DefaultProject, $"{_searchQuery} memory content");
        await SeedCodeEntryAsync("code-hash-shared", "src/Shared.cs", $"sealed class Shared {{ {_searchQuery} }}", 1, 3);
    }

    [Given("^a project with a code chunk spanning lines (\\d+) to (\\d+)$")]
    public Task GivenCodeChunkSpanningLines(int lineStart, int lineEnd) =>
        SeedCodeEntryAsync("code-hash-range", "src/Range.cs", $"sealed class RangeToken {{ {_searchQuery} }}", lineStart, lineEnd);

    [Given("^a project with code chunks$")]
    public Task GivenProjectWithCodeChunks() =>
        SeedCodeEntryAsync("code-hash-scope", "src/Scope.cs", $"sealed class ScopeToken {{ {_searchQuery} }}", 1, 2);

    [When("^I call memory_search for the project without a kind$")]
    public Task WhenSearchWithoutKind() => RunSearchAsync(DefaultProject, _searchQuery);

    [When("^I call memory_search for the project with kind \"([^\"]*)\"$")]
    public Task WhenSearchWithKind(string kind) => RunSearchAsync(DefaultProject, _searchQuery, kind: kind);

    [When("^I call memory_search for the project with kind \"([^\"]*)\" and scope \"([^\"]*)\"$")]
    public Task WhenSearchWithKindAndScope(string kind, string scope) => RunSearchAsync(DefaultProject, _searchQuery, kind: kind, scope: scope);

    [Then("^the response contains a \"results\" key with the memory hits$")]
    public void ThenResponseHasResultsKeyWithHits()
    {
        _lastSearchJson.ShouldNotBeNull();
        _lastSearchJson!.ContainsKey("results").ShouldBeTrue();
        _lastSearchJson["results"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Then("^the response contains no \"code\" key$")]
    public void ThenResponseHasNoCodeKey() => _lastSearchJson!.ContainsKey("code").ShouldBeFalse();

    [Then("^the response contains an empty \"results\" key$")]
    public void ThenResponseHasEmptyResultsKey()
    {
        _lastSearchJson!.ContainsKey("results").ShouldBeTrue();
        _lastSearchJson["results"]!.AsArray().Count.ShouldBe(0);
    }

    [Then("^the response contains a \"code\" key with the code hits$")]
    public void ThenResponseHasCodeKeyWithHits()
    {
        _lastSearchJson!.ContainsKey("code").ShouldBeTrue();
        _lastSearchJson["code"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Then("^the tool errors with invalid-params naming the allowed kinds$")]
    public void ThenToolErrorsInvalidParamsNamingKinds()
    {
        _lastError.ShouldNotBeNull("expected memory_search to refuse the invalid kind");
        _lastError!.Message.ShouldContain("invalid-params");
        _lastError.Message.ShouldContain("memory");
        _lastError.Message.ShouldContain("code");
        _lastError.Message.ShouldContain("both");
    }

    [Then("^the code hit carries lineStart (\\d+) and lineEnd (\\d+)$")]
    public void ThenCodeHitCarriesLineRange(int lineStart, int lineEnd)
    {
        var codeArray = _lastSearchJson!["code"]!.AsArray();
        codeArray.Count.ShouldBeGreaterThan(0);
        var hit = codeArray[0]!.AsObject();
        hit["lineStart"]!.GetValue<int>().ShouldBe(lineStart);
        hit["lineEnd"]!.GetValue<int>().ShouldBe(lineEnd);
    }

    [Then("^the code section is empty$")]
    public void ThenCodeSectionEmpty()
    {
        _lastSearchJson!.ContainsKey("code").ShouldBeTrue();
        _lastSearchJson["code"]!.AsArray().Count.ShouldBe(0);
    }

    // ── Rule: code_get reads one code chunk's full source by content hash ──

    [Given("^a code chunk ingested with a known content hash$")]
    public Task GivenCodeChunkWithKnownHash() =>
        SeedCodeEntryAsync("code-hash-known", "src/Known.cs", "sealed class KnownChunk { }", 4, 7);

    [When("^I call code_get for the project on that hash$")]
    public Task WhenCodeGetOnKnownHash() => RunCodeGetAsync(DefaultProject, "code-hash-known");

    [When("^I call code_get for the project on a hash nothing was ingested under$")]
    public Task WhenCodeGetOnUnknownHash() => RunCodeGetAsync(DefaultProject, "no-such-hash");

    [Then("^the response contains the chunk's value, path, and line range$")]
    public void ThenCodeGetResponseContainsChunk()
    {
        _lastCodeGetEnvelope.ShouldNotBeNull();
        var data = _lastCodeGetEnvelope!.Data!;
        data.Value.ShouldBe("sealed class KnownChunk { }");
        data.Path.ShouldBe("src/Known.cs");
        data.LineStart.ShouldBe(4);
        data.LineEnd.ShouldBe(7);
    }

    [Then("^the tool errors with unknown-hash$")]
    public void ThenToolErrorsUnknownHash() => _lastError.ShouldNotBeNull().ShouldBeOfType<UnknownHashException>();

    // ── Rule: ai-raccoon.ignore filters both ingestion pipelines the same way ──

    [Given("^a directory \"([^\"]*)\" with an \"ai-raccoon.ignore\" listing \"([^\"]*)\"$")]
    public async Task GivenDirectoryWithIgnoreFile(string virtualPath, string pattern)
    {
        var real = Ctx.MapPath(virtualPath);
        Directory.CreateDirectory(real);
        await File.WriteAllTextAsync(Path.Combine(real, IgnoreRulesProvider.FileName), pattern + "\n");
        await Ctx.SetWatchEnabledGlobalAsync(true);
        await Ctx.AddWatchScopeGlobalAsync(real);
    }

    [Given("^a file \"([^\"]*)\" under \"([^\"]*)\"$")]
    public void GivenFileUnder(string relativeFile, string virtualDir)
    {
        var dir = Ctx.MapPath(virtualDir);
        var path = Path.Combine(dir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        Ctx.WriteFile(path, "generated content");
        _lastFilePath = path;
    }

    [Given("^a watch for the project on path \"([^\"]*)\" with \"([^\"]*)\" already ingested into the code corpus$")]
    public async Task GivenWatchWithFileAlreadyIngestedIntoCode(string virtualPath, string fileName)
    {
        var dir = Ctx.MapPath(virtualPath);
        var filePath = Path.Combine(dir, fileName);
        Ctx.WriteFile(filePath, "sealed class Widget { }");
        await Ctx.SetupWatchAsync(DefaultProject, virtualPath);
        var ok = await Ctx.StepUntilAsync(async () => await CountCodeEntriesForPathAsync(filePath) > 0);
        ok.ShouldBeTrue("setup: the file did not land in the code corpus before the ignore rule existed");
        _lastFilePath = filePath;
    }

    [When("^I call memory_watch_add for the project on path \"([^\"]*)\"$")]
    public async Task WhenCallWatchAddForProject(string virtualPath)
    {
        _lastError = null;
        var target = Ctx.MapPath(virtualPath);
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            Directory.CreateDirectory(target);
        }

        await Ctx.SetWatchEnabledGlobalAsync(true);
        await Ctx.AddWatchScopeGlobalAsync(target);
        try
        {
            await Ctx.WatchToolsInstance.Add(DefaultProject, target);
            await Ctx.ReconcileOnceAsync();
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^I call memory_watch_add for the project on path \"([^\"]*)\" again$")]
    public async Task WhenCallWatchAddForProjectAgain(string virtualPath)
    {
        _lastError = null;
        try
        {
            await Ctx.WatchToolsInstance.Add(DefaultProject, Ctx.MapPath(virtualPath));
            await Ctx.ReconcileOnceAsync();
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^I call memory_ingest_directory for the project on path \"([^\"]*)\"$")]
    public async Task WhenCallIngestDirectory(string virtualPath)
    {
        _lastError = null;
        try
        {
            await Ctx.AddWatchScopeGlobalAsync(Ctx.MapPath(virtualPath));
            await Ctx.MemoryTools.IngestDirectory(DefaultProject, Ctx.MapPath(virtualPath));
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    private int _lastIngestIndexed;

    [When("^I call memory_ingest_file for the project on path \"([^\"]*)\"$")]
    public async Task WhenCallIngestFile(string virtualPath)
    {
        _lastError = null;
        try
        {
            var target = Ctx.MapPath(virtualPath);
            await Ctx.AddWatchScopeGlobalAsync(Path.GetDirectoryName(target)!);
            var result = await Ctx.MemoryTools.IngestFile(DefaultProject, target);
            _lastIngestIndexed = result.Data!.Indexed;
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^\"ai-raccoon.ignore\" is edited to list \"([^\"]*)\"$")]
    public async Task WhenIgnoreFileEditedToList(string pattern)
    {
        var ignorePath = Path.Combine(Ctx.RepoDir, IgnoreRulesProvider.FileName);
        Ctx.WriteFile(ignorePath, pattern + "\n");
        Ctx.Pipeline.Enqueue(new WatchEvent(DefaultProject, ignorePath, WatchEventKind.Changed));
        await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
        await Ctx.StepUntilAsync(async () => await CountCodeEntriesForPathAsync(_lastFilePath!) == 0);
    }

    [When("^\"ai-raccoon.ignore\" under \"([^\"]*)\" is edited$")]
    public async Task WhenIgnoreFileUnderPathEdited(string virtualPath)
    {
        // A probe file already fingerprinted, then corrupted in place: only a FULL re-scan
        // (watermark=null, WatchCatchUp.EnumerateFiles/IsDue) re-examines an already-fingerprinted,
        // mtime-unchanged file — a "changed since" scan would skip it. Recovering the corrupted
        // fingerprint back to the real content hash is proof a full re-scan, not a partial one, ran.
        var real = Ctx.MapPath(virtualPath);
        var probePath = Path.Combine(real, "rescan-probe.md");
        Ctx.WriteFile(probePath, "probe content");
        var seeded = await Ctx.StepUntilAsync(async () => await Ctx.WatchStore.GetFileHashAsync(DefaultProject, probePath) is not null);
        seeded.ShouldBeTrue("setup: the probe file never got fingerprinted");

        await using (var connection = await Ctx.OpenBankAsync())
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE watch_files SET file_hash = 'stale-corrupted-hash' WHERE project_id = @projectId AND path = @path",
                new { projectId = DefaultProject, path = IngestPath.Normalize(probePath) }));
        }

        _rescanProbePath = probePath;

        var ignorePath = Path.Combine(real, IgnoreRulesProvider.FileName);
        Ctx.WriteFile(ignorePath, "*.nonexistent-pattern\n");
        Ctx.Pipeline.Enqueue(new WatchEvent(DefaultProject, ignorePath, WatchEventKind.Changed));
        await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
    }

    [Then("^\"([^\"]*)\" is ingested into neither corpus$")]
    public async Task ThenFileIngestedIntoNeitherCorpus(string relativeFile)
    {
        var path = Path.Combine(Ctx.RepoDir, relativeFile.Replace('/', Path.DirectorySeparatorChar));
        (await CountMemoryEntriesForPathAsync(path)).ShouldBe(0);
        (await CountCodeEntriesForPathAsync(path)).ShouldBe(0);
    }

    [Then("^every file under \"([^\"]*)\" is ingested into neither corpus$")]
    public async Task ThenEveryFileUnderIngestedIntoNeitherCorpus(string subdir)
    {
        var path = Path.Combine(Ctx.RepoDir, subdir.TrimEnd('/'), "Debug", "app.dll");
        (await CountMemoryEntriesForPathAsync(path)).ShouldBe(0);
        (await CountCodeEntriesForPathAsync(path)).ShouldBe(0);
    }

    [Then("^the stale chunks for \"([^\"]*)\" are deleted from the code corpus$")]
    public async Task ThenStaleChunksDeletedFromCodeCorpus(string fileName) =>
        (await CountCodeEntriesForPathAsync(_lastFilePath!)).ShouldBe(0);

    [Then("^its last-change timestamp is updated without a new fingerprint$")]
    public async Task ThenLastChangeUpdatedWithoutNewFingerprint()
    {
        var hash = await Ctx.WatchStore.GetFileHashAsync(DefaultProject, _lastFilePath!);
        hash.ShouldBeNull("an ignored file must never carry a fingerprint");
    }

    [Then("^the call returns zero chunks$")]
    public void ThenCallReturnsZeroChunks() => _lastIngestIndexed.ShouldBe(0);

    [Then("^the watch performs a full re-scan of \"([^\"]*)\"$")]
    public async Task ThenWatchPerformsFullRescan(string virtualPath)
    {
        var expectedHash = WatchDigestExecutor.ComputeHash(IngestPath.Normalize(_rescanProbePath!), "probe content");
        var ok = await Ctx.StepUntilAsync(async () =>
            await Ctx.WatchStore.GetFileHashAsync(DefaultProject, _rescanProbePath!) == expectedHash);
        ok.ShouldBeTrue("the probe file's corrupted fingerprint was never corrected -- no full re-scan reached it");
    }

    // ── Rule: A broader watch wins; a narrower watch inside an existing one is rejected ──

    [Given("^a watch for the project on path \"([^\"]*)\"$")]
    public Task GivenWatchOnPath(string virtualPath) => Ctx.SetupWatchAsync(DefaultProject, virtualPath);

    [Then("^the watch on \"([^\"]*)\" is pruned$")]
    public async Task ThenWatchIsPruned(string virtualPath)
    {
        var status = await Ctx.WatchToolsInstance.Status(DefaultProject);
        status.Data!.Watches.Any(w => IngestPath.Normalize(w.Path) == IngestPath.Normalize(Ctx.MapPath(virtualPath)))
            .ShouldBeFalse();
    }

    [Then("^a single watch exists at \"([^\"]*)\" covering both corpora$")]
    public async Task ThenSingleWatchExistsCoveringBothCorpora(string virtualPath)
    {
        var status = await Ctx.WatchToolsInstance.Status(DefaultProject);
        status.Data!.Watches.Count(w => IngestPath.Normalize(w.Path) == IngestPath.Normalize(Ctx.MapPath(virtualPath)))
            .ShouldBe(1);

        var probe = Path.Combine(Ctx.MapPath(virtualPath), "probe.cs");
        Ctx.WriteFile(probe, "sealed class Probe { }");
        var ok = await Ctx.StepUntilAsync(async () => await CountCodeEntriesForPathAsync(probe) > 0);
        ok.ShouldBeTrue("the surviving watch never picked up a code file under it");
    }

    [Then("^the tool errors naming \"([^\"]*)\" as the covering watch$")]
    public void ThenToolErrorsNamingCoveringWatch(string virtualPath)
    {
        _lastError.ShouldNotBeNull().ShouldBeOfType<WatchOverlapException>();
        ((WatchOverlapException)_lastError!).CoveringPath.ShouldBe(IngestPath.Normalize(Ctx.MapPath(virtualPath)));
    }

    [Then("^no new watch is written$")]
    public async Task ThenNoNewWatchWritten()
    {
        var status = await Ctx.WatchToolsInstance.Status(DefaultProject);
        status.Data!.Watches.Any(w => IngestPath.Normalize(w.Path) == IngestPath.Normalize(Ctx.MapPath("/repo/docs")))
            .ShouldBeFalse();
    }

    [Then("^exactly one watch exists at \"([^\"]*)\"$")]
    public async Task ThenExactlyOneWatchExistsAt(string virtualPath)
    {
        var status = await Ctx.WatchToolsInstance.Status(DefaultProject);
        status.Data!.Watches.Count(w => IngestPath.Normalize(w.Path) == IngestPath.Normalize(Ctx.MapPath(virtualPath)))
            .ShouldBe(1);
    }

    [Then("^both watches exist independently$")]
    public async Task ThenBothWatchesExistIndependently()
    {
        var status = await Ctx.WatchToolsInstance.Status(DefaultProject);
        status.Data!.Watches.Count(w => IngestPath.Normalize(w.Path) == IngestPath.Normalize(Ctx.MapPath("/repo"))).ShouldBe(1);
        status.Data!.Watches.Count(w => IngestPath.Normalize(w.Path) == IngestPath.Normalize(Ctx.MapPath("/repo2"))).ShouldBe(1);
    }

    // ── Rule: Code files are always ingested, even with no code engine configured ──

    [Given("^a project with no code embedding engine configured$")]
    public void GivenNoCodeEngineConfigured()
    {
        // Default state: embedding.codeModel/embedding.codeEngine are never set for this scenario.
    }

    [When("^a code file is ingested$")]
    public async Task WhenCodeFileIngested()
    {
        _searchQuery = "zephyrcodewidget";
        await Ctx.AddWatchScopeGlobalAsync(Ctx.RepoDir);
        var path = Path.Combine(Ctx.RepoDir, "Widget.cs");
        Ctx.WriteFile(path, $"sealed class {_searchQuery} {{ }}");
        var result = await Ctx.MemoryTools.IngestFile(DefaultProject, path);
        _lastIngestIndexed = result.Data!.Indexed;
        _lastFilePath = path;
    }

    [Given("^code chunks stored pending$")]
    public Task GivenCodeChunksStoredPending() => WhenCodeFileIngested();

    [Then("^its chunks are stored in the code corpus with embed_state \"([^\"]*)\"$")]
    public async Task ThenChunksStoredWithEmbedState(string embedState)
    {
        await using var connection = await Ctx.OpenBankAsync();
        var states = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT embed_state FROM code_entries WHERE project_id = @projectId AND path = @path",
            new { projectId = DefaultProject, path = IngestPath.Normalize(_lastFilePath!) }))).ToList();
        states.ShouldNotBeEmpty();
        states.ShouldAllBe(s => s == embedState);
    }

    [Then("^the code section is served from full-text search only$")]
    public async Task ThenCodeSectionServedFromFtsOnly()
    {
        await RunSearchAsync(DefaultProject, _searchQuery, kind: "code");
        _lastSearchJson!["code"]!.AsArray().Count.ShouldBeGreaterThan(0);
    }

    [Then("^the response carries a warning that vector search is unavailable$")]
    public void ThenResponseCarriesVectorUnavailableWarning()
    {
        var warning = _lastSearchJson!["warning"]?.GetValue<string>();
        warning.ShouldNotBeNull().ShouldContain(CodeSearchWarnings.EngineNotConfigured);
    }

    [Given("^a project whose code embedding engine manifest cannot be loaded$")]
    public async Task GivenCodeEngineManifestCannotBeLoaded()
    {
        // B1: activation itself now validates the manifest, so a project whose engine is
        // unloadable at QUERY time (this scenario's real target — model files corrupt AFTER a
        // valid activation) needs a genuinely valid manifest here; FailOnCreateGenerator below is
        // what actually simulates the unloadable engine, independent of activation succeeding.
        var dir = Path.Combine(Ctx.DataRoot, "broken-code-engine");
        SeedManifestDirectory(dir, CodeCorpusSchema.EmbeddingDimensions);
        await Ctx.CodeEngineStore.ActivateCodeEngineAsync(dir);
        Ctx.FakeEmbeddingService.FailOnCreateGenerator = () => new InvalidOperationException("model file is corrupt");
    }

    [Then("^the tool errors with an actionable message naming the engine problem$")]
    public void ThenToolErrorsActionableMessageNamingEngineProblem()
    {
        _lastError.ShouldNotBeNull().ShouldBeOfType<CodeEngineUnloadableException>();
        _lastError!.Message.ShouldContain("model set code local");
    }

    [Then("^memory_search with kind \"([^\"]*)\" for the same project still succeeds$")]
    public async Task ThenSearchWithKindStillSucceeds(string kind)
    {
        var envelope = await Ctx.MemoryTools.Search(DefaultProject, _searchQuery, kind: kind);
        envelope.Data.ShouldNotBeNull();
    }

    // ── Rule: The code corpus's vector index accepts only 768-dimension manifests ──

    [Given("^a local model manifest declaring (\\d+) embedding dimensions$")]
    public void GivenLocalManifestDeclaringDimensions(int dims)
    {
        var dir = Path.Combine(Ctx.DataRoot, $"manifest-{dims}-{Guid.NewGuid():N}");
        SeedManifestDirectory(dir, dims);
        _manifestDir = dir;
    }

    [When("^the user runs model set code local against that manifest's directory$")]
    public Task WhenUserRunsModelSetCodeLocal() => RunModelSetCodeLocalAsync();

    [Then("^the command errors naming the required 768 dimensions$")]
    public void ThenCommandErrorsNaming768()
    {
        _lastError.ShouldNotBeNull("expected model set code local to refuse the manifest");
        _lastError!.Message.ShouldContain(CodeCorpusSchema.EmbeddingDimensions.ToString());
    }

    [Then("^no code engine setting is changed$")]
    public async Task ThenNoCodeEngineSettingChanged() =>
        (await Ctx.Store.GetSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBeNull();

    [Then("^the code embedding engine is activated$")]
    public async Task ThenCodeEngineActivated() =>
        (await Ctx.Store.GetSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldNotBeNull();

    // ── Rule: The code corpus never leaves the machine ──

    [Given("^a project with code chunks ingested$")]
    public Task GivenProjectWithCodeChunksIngested() =>
        SeedCodeEntryAsync("sync-code-hash", "src/Synced.cs", "sealed class Synced { }", 1, 1);

    [Given("^a project with code chunks ingested and a pending merge$")]
    public async Task GivenProjectWithCodeChunksAndPendingMerge()
    {
        await GivenProjectWithCodeChunksIngested();
        await SeedRemoteMemoryOnlySnapshotAsync("remote-hash", "remote content");
    }

    [When("^the project pushes a sync snapshot$")]
    public Task WhenProjectPushesSyncSnapshot() => RunSyncPushAsync();

    [When("^the merged snapshot is pushed$")]
    public Task WhenMergedSnapshotPushed() => RunSyncPushAsync();

    [Then("^the pushed snapshot contains no code_entries, code_fts, or vec_code table$")]
    public async Task ThenPushedSnapshotContainsNoCodeTables()
    {
        var tables = await TableNamesAsync(_pushedSnapshotPath!);
        tables.ShouldNotContain("code_entries");
        tables.ShouldNotContain("code_fts");
        tables.ShouldNotContain("vec_code");
    }

    [Given("^a remote snapshot carrying only memory entries$")]
    public async Task GivenRemoteSnapshotCarryingOnlyMemoryEntries()
    {
        await SeedCodeEntryAsync("local-code-hash", "src/Local.cs", "sealed class LocalCode { }", 1, 1);
        await SeedRemoteMemoryOnlySnapshotAsync("remote-only-hash", "remote only content");
    }

    [When("^the project pulls and merges that snapshot$")]
    public Task WhenProjectPullsAndMergesSnapshot() => RunSyncPushAsync();

    [Then("^the memory entries merge exactly as before$")]
    public async Task ThenMemoryEntriesMergeExactlyAsBefore()
    {
        await using var connection = await Ctx.OpenBankAsync();
        (await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM code_entries WHERE hash = 'local-code-hash'"))
            .ShouldBe(1L, "a pull/merge must never touch the local code corpus");
        (await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM entries WHERE hash = 'remote-only-hash'"))
            .ShouldBe(1L, "the remote memory entry must have merged in");
    }

    // ── Rule: A code engine change drains through the code-reindex job, not the memory outbox ──

    [Given("^a project with code chunks already embedded under the old engine$")]
    public async Task GivenCodeChunksEmbeddedUnderOldEngine()
    {
        // B1: activation now validates the manifest itself, so the "old engine" needs a genuinely
        // valid one too, not just an empty directory.
        var oldDir = Path.Combine(Ctx.DataRoot, "old-engine");
        SeedManifestDirectory(oldDir, CodeCorpusSchema.EmbeddingDimensions);
        await Ctx.CodeEngineStore.ActivateCodeEngineAsync(oldDir);
        await SeedEmbeddedCodeEntryAsync("old-embedded-hash", "src/Old.cs", "sealed class Old { }", 1, 1);
    }

    [When("^the user runs model set code local against a new manifest$")]
    public async Task WhenUserRunsModelSetCodeLocalAgainstNewManifest()
    {
        var newDir = Path.Combine(Ctx.DataRoot, "new-engine");
        SeedManifestDirectory(newDir, CodeCorpusSchema.EmbeddingDimensions);
        _manifestDir = newDir;
        await RunModelSetCodeLocalAsync();
    }

    [Then("^every code row's embed_state becomes pending in the same transaction as the settings change$")]
    public async Task ThenEveryCodeRowPendingInSameTransaction()
    {
        await using var connection = await Ctx.OpenBankAsync();
        var states = (await connection.QueryAsync<string>("SELECT embed_state FROM code_entries")).ToList();
        states.ShouldNotBeEmpty();
        states.ShouldAllBe(s => s == "pending");
        (await Ctx.Store.GetSettingAsync(EmbeddingSettingsKeys.CodeModel)).ShouldBe(Path.GetFullPath(_manifestDir!));
    }

    [Given("^a code engine change with pending rows still draining$")]
    public async Task GivenCodeEngineChangeWithPendingRowsDraining()
    {
        await GivenCodeChunksEmbeddedUnderOldEngine();
        await WhenUserRunsModelSetCodeLocalAgainstNewManifest();
        // Deliberately never runs the reindex job here -- rows stay pending, simulating the drain window.
    }

    [When("^memory_search is called for the project with kind \"([^\"]*)\"$")]
    public Task WhenMemorySearchIsCalledForProjectWithKind(string kind) => RunSearchAsync(DefaultProject, _searchQuery, kind: kind);

    [Then("^the call succeeds without a migration-in-progress refusal$")]
    public void ThenCallSucceedsWithoutMigrationRefusal()
    {
        _lastError.ShouldBeNull();
        _lastSearchEnvelope!.Data.ShouldNotBeNull();
    }

    [Given("^code rows left pending by a code engine change$")]
    public async Task GivenCodeRowsLeftPendingByEngineChange()
    {
        await GivenCodeChunksEmbeddedUnderOldEngine();
        await WhenUserRunsModelSetCodeLocalAgainstNewManifest();
        _searchQuery = "codereindexprobe";
        await using var connection = await Ctx.OpenBankAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET value = @value, path = @path, source_file = @path WHERE hash = 'old-embedded-hash'",
            new { value = $"sealed class {_searchQuery} {{ }}", path = "src/Reindexed.cs" }));
    }

    /// <summary>
    ///     WP11-B2: RunAsync now only signals the embed topic — draining is EmbedDrainService's own
    ///     job. This step still describes the user-observable outcome ("the maintenance job runs"
    ///     and rows end up re-embedded), so it drains the signal it just queued the same way the
    ///     real consumer would, rather than embedding inline itself.
    /// </summary>
    [When("^the code-reindex maintenance job runs$")]
    public async Task WhenCodeReindexJobRuns()
    {
        await using var connection = await Ctx.OpenBankAsync();
        await Ctx.ReindexJob.RunAsync(connection, CancellationToken.None);
        if (Ctx.EmbedDrainPump.DrainUpTo(1).Count > 0)
        {
            await Ctx.CodeEmbedder.EmbedPendingBatchAsync(connection, BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun, CancellationToken.None);
        }
    }

    [Then("^the pending rows are re-embedded under the new engine$")]
    public async Task ThenPendingRowsReEmbeddedUnderNewEngine()
    {
        await using var connection = await Ctx.OpenBankAsync();
        var states = (await connection.QueryAsync<string>("SELECT embed_state FROM code_entries")).ToList();
        states.ShouldNotBeEmpty();
        states.ShouldAllBe(s => s == "embedded");
    }

    [Then("^kind=code search returns vector-ranked results for them again$")]
    public async Task ThenKindCodeSearchReturnsVectorRankedResults()
    {
        await RunSearchAsync(DefaultProject, _searchQuery, kind: "code");
        _lastSearchJson!["code"]!.AsArray().Count.ShouldBeGreaterThan(0);
        var warning = _lastSearchJson["warning"]?.GetValue<string>();
        (warning is null || !warning.Contains(CodeSearchWarnings.EngineNotConfigured, StringComparison.Ordinal))
            .ShouldBeTrue("the code section must no longer degrade to FTS5-only now that the engine is configured and drained");
    }

    // ── Rule: Code and code/both searches are excluded from search-quality recording ──

    [Then("^no search_quality row is written for that call$")]
    public async Task ThenNoSearchQualityRowWritten()
    {
        await using var connection = await Ctx.OpenBankAsync();
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM search_quality")).ShouldBe(0L);
    }

    [Then("^a search_quality row is written for that call$")]
    public async Task ThenSearchQualityRowWritten()
    {
        await using var connection = await Ctx.OpenBankAsync();
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM search_quality")).ShouldBeGreaterThan(0L);
    }
}
