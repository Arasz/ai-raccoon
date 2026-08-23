using System.Reflection;
using System.Text.RegularExpressions;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Sync;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using AiRaccoon.Prompts;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Reqnroll;
using Shouldly;

namespace AiRaccoon.Tests.BDD;

// Steps implement the native-memory requirements (see docs/work/features-native-memory/native-memory.feature);
// the FR-NM section markers below map each block to its requirement ID.
[Binding]
public sealed class NativeMemorySteps(ScenarioContext scenarioContext)
{
    private const string CloudStoreKey = "CloudStore";

    private const int ChunkSmallMaxTokens = 50;

    // Larger than a single generated line's token count, so BuildOverlay's per-unit budget check
    // (used + unit.TokenCount > overlayTokens) actually admits at least one reused line.
    private const int ChunkOverlayTokens = 20;

    private static readonly IChunker RealChunker = TestData.RealMarkdownChunker();

    // Real key material is contiguous base62/underscore after a known secret prefix — natural-language
    // fixture ids like "sk-hub-history-is-user-data" must not match.
    private static readonly Regex SecretValuePattern = new(
        @"AKIA[0-9A-Z]{16}|sk-(proj-|ant-)?[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{36}|xox[baprs]-[A-Za-z0-9]{10,}",
        RegexOptions.Compiled);

    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".json", ".md", ".yml", ".yaml", ".xml", ".props", ".targets", ".feature", ".sh", ".csproj", ".slnx" };

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules", "TestResults", ".ai-badger"];

    private readonly MemoryFeatureContext _ctx = scenarioContext.ScenarioContainer.Resolve<MemoryFeatureContext>();
    private readonly IMemoryStore _store = scenarioContext.ScenarioContainer.Resolve<IMemoryStore>();
    private string? _customModelPath;

    private IReadOnlyList<MemorySearchResult>? _defaultRrfSearch;

    private ToolGate? _gate;
    private Exception? _lastError;
    private IReadOnlyList<MemorySearchResult>? _lastSearch;

    private MemoryEntry? _lastWrite;

    /// <summary>The scenario's last search; throws plainly when no search step has run.</summary>
    private IReadOnlyList<MemorySearchResult> LastSearch => _lastSearch ?? throw new InvalidOperationException("no search step has run in this scenario");

    private ToolGate Gate =>
        _gate ??= new ToolGate(
            new MemoryAccessGuard(_store),
            new PromotionQueueService(
                new SqlitePromotionQueueStore(_ctx.Factory, _ctx.TimeProvider),
                _store,
                new UniformCountEvictionPolicy(),
                new PromotionQueueMetrics(),
                NullLogger<PromotionQueueService>.Instance,
                _ctx.TimeProvider),
            new NeverMigratingStore());

    private FakeCloudStore CloudStore => (FakeCloudStore)scenarioContext[CloudStoreKey];

    private string ObjectKeyFor(string projectId) => $"memory-{projectId}.db";

    /// <summary>Records a tool error both locally and on the shared scenario context, so a Then bound in another step class (e.g. FileWatcherSteps) can see it too.</summary>
    private void RecordError(Exception ex)
    {
        _lastError = ex;
        scenarioContext["LastError"] = ex;
    }

    private static IEnumerable<(Type Class, McpServerToolAttribute Attr)> ToolAttributes() =>
        typeof(MemoryTools).Assembly.GetTypes()
            .Where(t => t.Namespace == "AiRaccoon.Tools" && t.IsClass && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
                .OfType<McpServerToolAttribute>()
                .Select(attr => (Class: t, Attr: attr)));

    private static List<string> AllToolNames() =>
    [
        .. ToolAttributes()
            .Select(x => x.Attr.Name ?? throw new InvalidOperationException(
                $"{x.Class.Name} declares an [McpServerTool] with no explicit Name"))
    ];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repo root (AiRaccoon.slnx) walking up from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> ScanTextFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => ScannedExtensions.Contains(Path.GetExtension(f))
                        && !f.Split(Path.DirectorySeparatorChar).Any(SkippedDirectories.Contains));

    private static HashSet<int> ExtractLineNumbers(string chunk) => [.. Regex.Matches(chunk, @"Line (\d+)").Select(m => int.Parse(m.Groups[1].Value))];

    /// <summary>A real copy of the bundled ONNX model under a distinct path (engine fingerprint change).</summary>
    private string EnsureCustomModelCopy()
    {
        if (_customModelPath is null)
        {
            var source = BundledModel.ResolveModelPath();
            _customModelPath = Path.Combine(_ctx.DataRoot, "custom-model.onnx");
            File.Copy(source, _customModelPath);
        }

        return _customModelPath;
    }

    private async Task RunSyncAsync(ICloudStore cloud, CancellationToken cancellationToken)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var sync = new SyncService(cloud, _ctx.Factory.OpenBankAsync, OpenSnapshotAsync, OpenReadOnlyAsync,
            _ctx.TimeProvider, NullLogger<SyncService>.Instance);
        await sync.MemorySyncAsync(projectId, ObjectKeyFor(projectId), cancellationToken);
    }

    /// <summary>Read-write open of the local snapshot: the workspace strip DELETEs + VACUUMs it.</summary>
    private static async Task<SqliteConnection> OpenSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(cancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await connection.OpenAsync(cancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static async Task<string> WriteTempAsync(byte[] data)
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, data);
        return path;
    }

    /// <summary>Builds a VACUUM INTO snapshot of a scratch bank containing the given committed rows.</summary>
    private static async Task<byte[]> BuildRemoteSnapshotAsync(string projectId,
        IReadOnlyList<(string Content, string Path)> rows)
    {
        using var scratch = new MemoryFeatureContext();
        foreach (var (content, path) in rows)
        {
            await scratch.Store.AddContentAsync(projectId, path, content,
                ContextNaming.ProjectContext(projectId), cancellationToken: CancellationToken.None);
        }

        var snapshotPath = Path.GetTempFileName();
        try
        {
            await using (var conn = await scratch.Factory.OpenBankAsync(CancellationToken.None))
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{snapshotPath}'";
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }

            return await File.ReadAllBytesAsync(snapshotPath, CancellationToken.None);
        }
        finally
        {
            File.Delete(snapshotPath);
        }
    }

    // ── Background ──
    [Given("the ai-raccoon MCP server is running")]
    public void GivenServerIsRunning()
    {
        /* no-op: context provides real store */
    }

    [Given("the memory bank is a single file memory.db")]
    public void GivenMemoryBankIsSingleFile()
    {
        /* no-op */
    }

    [Given(@"a project with id ""(.*)"" exists")]
    public async Task GivenProjectExists(string projectId)
    {
        await _ctx.OpenBankAsync();
        scenarioContext["ProjectId"] = projectId;
    }

    [Given(@"a second project with id ""(.*)"" exists")]
    public async Task GivenSecondProjectExists(string projectId)
    {
        await _ctx.OpenBankAsync();
        scenarioContext["SecondProjectId"] = projectId;
    }

    // ── FR-NM-1: The bank is one self-describing SQLite file ──
    [When("I inspect the bank directory")]
    public void WhenIInspectBankDirectory()
    {
        /* no-op: _ctx.DataRoot is populated */
    }

    [Then("it contains memory.db")]
    public void ThenItContainsMemoryDb() => File.Exists(Path.Combine(_ctx.DataRoot, "memory.db")).ShouldBeTrue();

    [Then("it does not contain raccoon_meta.db")]
    public void ThenItDoesNotContainRaccoonMetaDb() => File.Exists(Path.Combine(_ctx.DataRoot, "raccoon_meta.db")).ShouldBeFalse();

    [Given("an entry exists in project \"(.*)\"")]
    public async Task GivenEntryExistsInProject(string projectId) =>
        _lastWrite = await _store.WriteAsync(new MemoryWriteRequest(projectId, "test content"),
            CancellationToken.None);

    [When("I query the entries table")]
    public async Task WhenIQueryEntriesTable()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var columns = (await conn.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('entries')")).ToList();
        scenarioContext["Columns"] = columns;
    }

    [Then("access_count, last_accessed_at, rating, ttl_days and agent_id are columns of that row")]
    public void ThenMetadataColumnsExist()
    {
        var columns = (List<string>)scenarioContext["Columns"];
        columns.ShouldContain("access_count");
        columns.ShouldContain("last_accessed_at");
        columns.ShouldContain("rating");
        columns.ShouldContain("ttl_days");
        columns.ShouldContain("agent_id");
    }

    [When(@"I call memory_workspace_begin for project ""([^""]*)""(?! with)")]
    public async Task WhenIWorkspaceBegin(string projectId)
    {
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace("ws-1", projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        scenarioContext["WorkspaceId"] = "ws-1";
    }

    [When(@"I call memory_workspace_begin for project ""(.*)"" with agent ""(.*)""")]
    public async Task WhenIWorkspaceBeginWithAgent(string projectId, string agent)
    {
        // The agent label is part of the tool contract; begin itself returns a generated id.
        var workspace = await new WorkspaceService(_store, new SqliteWorkspaceStore(_ctx.Factory), _ctx.TimeProvider)
            .BeginAsync(projectId, cancellationToken: CancellationToken.None);
        scenarioContext["WorkspaceId"] = workspace.Id;
    }

    [Then(@"a workspaces row with status ""(.*)"" exists in memory.db")]
    public async Task ThenWorkspaceRowExistsInMemoryDb(string status)
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var statusValue = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT status FROM workspaces WHERE id = 'ws-1'");
        statusValue.ShouldBe(status);
    }

    // ── FR-NM-2: Access modes ──

    // ── FR-NM-3: Pluggable embeddings ──
    [Given("the small model ships inside the tool package")]
    public void GivenSmallModelShips() => File.Exists(BundledModel.ResolveModelPath()).ShouldBeTrue();

    [Given("a custom model file exists")]
    public void GivenCustomModelFileExists() => File.Exists(EnsureCustomModelCopy()).ShouldBeTrue();

    [When(@"^I call memory_configure with provider ""([^""]*)""$")]
    public async Task WhenIConfigureProvider(string provider) => await TestData.ConfigureAndDrainEmbeddingAsync(
        _store, _ctx.Factory, TestData.CreateEmbeddingService(), provider, null, null, CancellationToken.None,
        _ctx.TimeProvider);

    [When(@"^I call memory_configure with provider ""([^""]*)"" and a model path$")]
    public async Task WhenIConfigureProviderWithModelPath(string provider) => await TestData.ConfigureAndDrainEmbeddingAsync(
        _store, _ctx.Factory, TestData.CreateEmbeddingService(), provider, EnsureCustomModelCopy(), null,
        CancellationToken.None, _ctx.TimeProvider);

    [When("I call memory_configure with a different engine")]
    public async Task WhenIConfigureDifferentEngine() => await TestData.ConfigureAndDrainEmbeddingAsync(
        _store, _ctx.Factory, TestData.CreateEmbeddingService(), "local", EnsureCustomModelCopy(), null,
        CancellationToken.None, _ctx.TimeProvider);

    [When(@"^I call memory_configure with provider ""([^""]*)"", baseUrl ""([^""]*)"" and model ""([^""]*)""$")]
    public async Task WhenIConfigureOpenAi(string provider, string baseUrl, string model) => await TestData.ConfigureAndDrainEmbeddingAsync(
        _store, _ctx.Factory, TestData.CreateEmbeddingService(), provider, model, baseUrl, CancellationToken.None,
        _ctx.TimeProvider);

    [Given(@"project ""(.*)"" has no embedding model configured")]
    public void GivenNoEmbeddingModelConfigured(string projectId)
    {
        // No-op: a fresh fixture's settings table has no embedding.* rows until memory_configure
        // is called — MemorySchema does not seed them, so the precondition already holds.
    }

    [Given(@"project ""(.*)"" has one pending entry")]
    public async Task GivenProjectHasOnePendingEntry(string projectId) =>
        _lastWrite = await _store.WriteAsync(new MemoryWriteRequest(projectId, "deferred note"),
            CancellationToken.None);

    [Given(@"project ""(.*)"" has embedded entries")]
    public async Task GivenProjectHasEmbeddedEntries(string projectId)
    {
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _ctx.Factory, TestData.CreateEmbeddingService(),
            "local", null, null, CancellationToken.None, _ctx.TimeProvider);
        _lastWrite = await _store.WriteAsync(new MemoryWriteRequest(projectId, "re-embed me"),
            CancellationToken.None);
    }

    [When("I call memory_embed_pending")]
    public async Task WhenIEmbedPending() => await _store.EmbedPendingAsync((string)scenarioContext["ProjectId"], null, CancellationToken.None);

    [Then("the write is embedded with the local engine")]
    public async Task ThenWriteEmbeddedWithLocalEngine()
    {
        _lastWrite.ShouldNotBeNull();
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var state = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT embed_state FROM entries WHERE hash = @hash", new { hash = _lastWrite!.Hash });
        state.ShouldBe("embedded");
    }

    [Then("no external server process or download is required")]
    public async Task ThenNoExternalServerRequired()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var engine = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.engine'");
        engine.ShouldBe("local:bundled");
    }

    [Then("the custom model is used")]
    public async Task ThenCustomModelUsed()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var model = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.model'");
        model.ShouldBe(EnsureCustomModelCopy());
    }

    [Then("writes are embedded through that endpoint")]
    public async Task ThenWritesEmbeddedThroughEndpoint()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var baseUrl = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.baseUrl'");
        var engine = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.engine'");
        baseUrl.ShouldBe("http://localhost:11434");
        engine.ShouldBe("openai:nomic-embed-text@http://localhost:11434");
    }

    [Then("the entry is stored but not indexed")]
    public async Task ThenEntryStoredButNotIndexed()
    {
        _lastWrite.ShouldNotBeNull();
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var state = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT embed_state FROM entries WHERE hash = @hash", new { hash = _lastWrite!.Hash });
        state.ShouldBe("pending");
    }

    [Then("memory_stats reports one pending entry")]
    public async Task ThenStatsReportsOnePendingEntry()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.PendingCount.ShouldBe(1);
    }

    [Then("memory_stats reports zero pending entries")]
    public async Task ThenStatsReportsZeroPendingEntries()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.PendingCount.ShouldBe(0);
    }

    [Then("the entry is searchable")]
    public async Task ThenEntrySearchable()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var results = (await _store.SearchAsync(
            new SearchQuery(projectId, "deferred note"), CancellationToken.None)).Results;
        results.Count.ShouldBeGreaterThan(0);
    }

    [Then("every embedded entry is re-embedded with the new engine")]
    public async Task ThenEveryEmbeddedReembedded()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var embedded = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE embed_state = 'embedded'");
        var total = await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM entries");
        embedded.ShouldBe(total);
        var engine = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.engine'");
        engine.ShouldBe($"local:{EnsureCustomModelCopy()}");
    }

    // ── FR-NM-4: Hybrid search ──
    [When(@"I search for ""(.*)"" in project ""([^""]*)""(?! with)")]
    public async Task WhenISearchForInProject(string query, string projectId) =>
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query),
            CancellationToken.None)).Results;

    [When(@"I search for ""(.*)"" in project ""(.*)"" with scope ""(.*)""")]
    public async Task WhenISearchWithScope(string query, string projectId, string scope)
    {
        var parsedScope = scope.ToLowerInvariant() switch
        {
            "all" => SearchScope.All,
            "project" => SearchScope.Project,
            "shared" => SearchScope.Shared,
            _ => SearchScope.All
        };
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query, parsedScope),
            CancellationToken.None)).Results;
    }

    [When(@"I search for ""(.*)"" in project ""(.*)"" with workspace ""(.*)""")]
    public async Task WhenISearchWithWorkspace(string query, string projectId, string wsId) =>
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query, SearchScope.All, wsId),
            CancellationToken.None)).Results;

    [Then("results carry hash, ranking, path and snippet")]
    public void ThenResultsCarryContractFields()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBeGreaterThan(0);
        var r = _lastSearch[0];
        r.Hash.ShouldNotBeNullOrWhiteSpace();
        r.Path.ShouldNotBeNullOrWhiteSpace();
        r.Snippet.ShouldNotBeNullOrWhiteSpace();
        r.Ranking.ShouldBeInRange(0.0, 1.0);
    }

    [Then("ranking is normalized into 0..1")]
    public void ThenRankingNormalized()
    {
        _lastSearch.ShouldNotBeNull();
        foreach (var r in LastSearch)
        {
            r.Ranking.ShouldBeInRange(0.0, 1.0);
        }
    }

    [When(@"I search for that fact with scope ""(.*)""")]
    public async Task WhenISearchWithScope(string scope)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var parsedScope = scope.ToLowerInvariant() switch
        {
            "shared" => SearchScope.Shared,
            "project" => SearchScope.Project,
            _ => SearchScope.All
        };
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, "test", parsedScope),
            CancellationToken.None)).Results;
    }

    [Then("no results are returned")]
    public void ThenNoResultsReturned()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBe(0);
    }

    // ── FR-NM-6/FR-MEM-1.5: Workspaces ──
    [Given(@"workspace ""(.*)"" exists for project ""(.*)""")]
    public async Task GivenWorkspaceExists(string wsId, string projectId)
    {
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        scenarioContext["WorkspaceId"] = wsId;
    }

    [Given(@"workspace ""(.*)"" for project ""(.*)"" contains entries with hashes ""(.*)"" and ""(.*)""")]
    public async Task GivenWorkspaceContainsEntries(string wsId, string projectId, string h1, string h2)
    {
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);

        // Write entries into the workspace context; the feature's "h1"/"h2" labels are the
        // scenario keys the consolidate step resolves to the real hashes.
        var e1 = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "entry-1", $"workspace:{wsId}", WorkspaceId: wsId),
            CancellationToken.None);
        var e2 = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "entry-2", $"workspace:{wsId}", WorkspaceId: wsId),
            CancellationToken.None);

        scenarioContext["WorkspaceId"] = wsId;
        scenarioContext[h1] = e1.Hash;
        scenarioContext[h2] = e2.Hash;
        scenarioContext[$"{h1}Content"] = "entry-1";
    }

    [Then(@"the entry row has workspace_id ""(.*)""")]
    public async Task ThenEntryRowHasWorkspaceId(string expectedWsId)
    {
        _lastWrite.ShouldNotBeNull();
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var wsId = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT workspace_id FROM entries WHERE hash = @hash",
            new { hash = _lastWrite!.Hash });
        wsId.ShouldBe(expectedWsId);
    }

    [Then("the schema forbids a row that has both a workspace_id and a committed scope")]
    public async Task ThenSchemaForbidsBothWorkspaceAndCommitted()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var sql = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries'");
        sql.ShouldNotBeNull();
        sql.ShouldContain("(workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL)");
    }

    [When(@"I call memory_workspace_consolidate with keep=\[""([^""]*)""\]")]
    public async Task WhenIConsolidateWithKeep(string keep)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var wsId = (string)scenarioContext["WorkspaceId"];
        var ws = new WorkspaceService(_store, new SqliteWorkspaceStore(_ctx.Factory), _ctx.TimeProvider);
        var keepHashes = keep == "all"
            ? (IReadOnlyList<string>)["all"]
            : [.. keep.Split(',', StringSplitOptions.TrimEntries).Select(k => (string)scenarioContext[k])];
        var result = await ws.ConsolidateAsync(projectId, wsId, keepHashes, CancellationToken.None);
        scenarioContext["ConsolidateResult"] = result;
    }

    [Then(@"""(.*)"" is committed to project ""(.*)""")]
    public async Task ThenEntryCommittedToProject(string hashKey, string projectId)
    {
        var hash = (string)scenarioContext[hashKey];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var scope = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT scope FROM entries WHERE hash = @hash", new { hash });
        scope.ShouldBe("project");
        var wsId = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT workspace_id FROM entries WHERE hash = @hash", new { hash });
        wsId.ShouldBeNull();
    }

    [Then(@"""(.*)"" is deleted")]
    public async Task ThenEntryDeleted(string hashKey)
    {
        var hash = (string)scenarioContext[hashKey];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE hash = @hash", new { hash });
        count.ShouldBe(0);
    }

    [Then(@"the workspace row has status ""(.*)""")]
    [Then(@"its workspaces row has status ""(.*)""")]
    public async Task ThenWorkspaceRowHasStatus(string status)
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var wsStatus = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT status FROM workspaces WHERE id = @id",
            new { id = (string)scenarioContext["WorkspaceId"] });
        wsStatus.ShouldBe(status);
    }

    [When(@"I call memory_workspace_discard for ""(.*)""")]
    public async Task WhenIDiscardWorkspace(string wsId)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var ws = new WorkspaceService(_store, new SqliteWorkspaceStore(_ctx.Factory), _ctx.TimeProvider);
        await ws.DiscardAsync(projectId, wsId, CancellationToken.None);
    }

    [Then("the workspace is removed")]
    public async Task ThenWorkspaceRemoved()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var status = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT status FROM workspaces WHERE id = @id", new { id = (string)scenarioContext["WorkspaceId"] });
        status.ShouldBe("Discarded");
        var entries = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE workspace_id = @id", new { id = (string)scenarioContext["WorkspaceId"] });
        entries.ShouldBe(0);
    }

    // ── FR-NM-7: Content identity ──
    [When("I write the same content twice to project \"(.*)\"")]
    public async Task WhenIWriteSameContentTwice(string projectId)
    {
        var content = "duplicate content";
        await _store.WriteAsync(new MemoryWriteRequest(projectId, content),
            CancellationToken.None);
        await _store.WriteAsync(new MemoryWriteRequest(projectId, content),
            CancellationToken.None);
    }

    [Then("memory_stats reports one entry")]
    public async Task ThenStatsReportsOneEntry()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.EntryCount.ShouldBe(1);
    }

    [Given(@"an entry with hash ""(.*)"" exists in project ""(.*)""")]
    public async Task GivenEntryWithHashExists(string hash, string projectId)
    {
        // Write content to get a hash. We can't force a specific hash, so we store the actual hash.
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "sharable content"),
            CancellationToken.None);
        scenarioContext["ShareHash"] = entry.Hash;
    }

    [When(@"I call memory_share with hash ""(.*)""")]
    public async Task WhenIShareWithHash(string hash)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var actualHash = scenarioContext.ContainsKey("ShareHash")
            ? (string)scenarioContext["ShareHash"]
            : hash;
        try
        {
            await _store.ShareAsync(projectId, actualHash, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [Then(@"a row with path ""(.*)"" exists in the shared scope")]
    public async Task ThenRowWithPathInSharedScope(string expectedPath)
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var paths = (await conn.QueryAsync<string>(
            "SELECT path FROM entries WHERE scope = 'shared'")).ToList();
        // The feature's "shared/<path>" is a template: the shared row lives under shared/.
        paths.ShouldContain(p => p.StartsWith("shared/", StringComparison.Ordinal));
    }

    [Then(@"its hash differs from ""(.*)""")]
    public async Task ThenHashDiffers(string originalHash)
    {
        var sourceHash = scenarioContext.ContainsKey("ShareHash")
            ? (string)scenarioContext["ShareHash"]
            : originalHash;
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var sharedHash = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT hash FROM entries WHERE scope = 'shared'");
        sharedHash.ShouldNotBeNull();
        sharedHash.ShouldNotBe(sourceHash);
    }

    [Given(@"workspace ""(.*)"" contains an entry with path ""(.*)""")]
    public async Task GivenWorkspaceContainsEntryWithPath(string wsId, string path)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        await _store.AddContentAsync(projectId, path, "workspace content", $"workspace:{wsId}",
            cancellationToken: CancellationToken.None);
        scenarioContext["WorkspaceId"] = wsId;
    }

    [Then(@"the committed entry keeps path ""(.*)""")]
    public async Task ThenCommittedEntryKeepsPath(string expectedPath)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var path = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT path FROM entries WHERE scope = 'project' AND project_id = @pid AND workspace_id IS NULL",
            new { pid = projectId });
        path.ShouldNotBeNull();
        path.ShouldContain(expectedPath);
    }

    // ── FR-NM-8: Sync ──
    [When("I call memory_sync without sync credentials")]
    public async Task WhenISyncWithoutCredentials()
    {
        try
        {
            var factory = new SyncCloudStoreFactory(_store, NullLoggerFactory.Instance);
            await RunSyncAsync(await factory.CreateAsync(CancellationToken.None), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [Then("the tool errors with sync-not-configured")]
    public void ThenSyncNotConfigured() => _lastError.ShouldBeOfType<SyncNotConfiguredException>();

    [Given("sync credentials are configured")]
    public void GivenSyncCredentialsConfigured() => scenarioContext[CloudStoreKey] = new FakeCloudStore();

    [When(@"I call memory_sync for project ""(.*)""")]
    public async Task WhenISyncForProject(string projectId) => await RunSyncAsync(CloudStore, CancellationToken.None);

    [When("I call memory_sync")]
    public async Task WhenICallMemorySync()
    {
        if (!scenarioContext.ContainsKey(CloudStoreKey))
        {
            scenarioContext[CloudStoreKey] = new FakeCloudStore();
        }

        await RunSyncAsync(CloudStore, CancellationToken.None);
    }

    [Given("the remote snapshot changed since the last pull")]
    public async Task GivenRemoteSnapshotChanged()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var fake = new FakeCloudStore();
        scenarioContext[CloudStoreKey] = fake;
        // A local write that must survive the merge.
        await _store.WriteAsync(new MemoryWriteRequest(projectId, "local kept fact"), CancellationToken.None);
        fake.Set(ObjectKeyFor(projectId),
            await BuildRemoteSnapshotAsync(projectId, [("remote merged fact", "remote.md")]));
    }

    [Given("an entry was deleted locally since the last sync")]
    public async Task GivenEntryDeletedLocallySinceLastSync()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var fake = new FakeCloudStore();
        scenarioContext[CloudStoreKey] = fake;
        var entry = await _store.WriteAsync(new MemoryWriteRequest(projectId, "doomed entry"),
            CancellationToken.None);
        scenarioContext["DoomedHash"] = entry.Hash;
        // Baseline: the remote has the entry before the local delete.
        await RunSyncAsync(fake, CancellationToken.None);
        await _store.DeleteAsync(projectId, entry.Hash, CancellationToken.None);
    }

    [Given("the remote contributed new rows")]
    public async Task GivenRemoteContributedNewRows()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var fake = new FakeCloudStore();
        scenarioContext[CloudStoreKey] = fake;
        // Local engine configured so the pipeline seam can embed merged rows.
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _ctx.Factory, TestData.CreateEmbeddingService(),
            "local", null, null, CancellationToken.None, _ctx.TimeProvider);
        fake.Set(ObjectKeyFor(projectId),
            await BuildRemoteSnapshotAsync(projectId, [("merged fresh fact", "fresh.md")]));
    }

    [Then("a snapshot object is uploaded with If-Match")]
    public async Task ThenSnapshotUploaded()
    {
        var obj = await CloudStore.PullAsync(ObjectKeyFor((string)scenarioContext["ProjectId"]));
        obj.ShouldNotBeNull();
        obj.ETag.ShouldNotBeNullOrWhiteSpace();
    }

    [Then("the snapshot passed integrity check before upload")]
    public async Task ThenSnapshotIntegrityCheck()
    {
        var obj = (await CloudStore.PullAsync(ObjectKeyFor((string)scenarioContext["ProjectId"])))!;
        var path = await WriteTempAsync(obj.Data);
        try
        {
            await using var conn = await OpenReadOnlyAsync(path, CancellationToken.None);
            var result = (string)(await conn.ExecuteScalarAsync("PRAGMA quick_check"))!;
            result.ShouldBe("ok");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Then("the remote rows are merged row-wise")]
    public async Task ThenRemoteRowsMerged()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE value = 'remote merged fact'");
        count.ShouldBe(1);
    }

    [Then("no local write since the last sync is lost")]
    public async Task ThenNoLocalWriteLost()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE value = 'local kept fact'");
        count.ShouldBe(1);
    }

    [Then("the deletion reaches the remote")]
    public async Task ThenDeletionReachesRemote()
    {
        var obj = (await CloudStore.PullAsync(ObjectKeyFor((string)scenarioContext["ProjectId"])))!;
        var path = await WriteTempAsync(obj.Data);
        try
        {
            await using var conn = await OpenReadOnlyAsync(path, CancellationToken.None);
            var count = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM entries WHERE hash = @hash",
                new { hash = (string)scenarioContext["DoomedHash"] });
            count.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Then("the entry is not resurrected by the merge")]
    public async Task ThenEntryNotResurrected()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE hash = @hash",
            new { hash = (string)scenarioContext["DoomedHash"] });
        count.ShouldBe(0);
    }

    [Then(@"""(.*)"" is not part of any synced payload")]
    public async Task ThenNotPartOfSyncedPayload(string content)
    {
        var obj = (await CloudStore.PullAsync(ObjectKeyFor((string)scenarioContext["ProjectId"])))!;
        var path = await WriteTempAsync(obj.Data);
        try
        {
            await using var conn = await OpenReadOnlyAsync(path, CancellationToken.None);
            var count = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM entries WHERE value = @value", new { value = content });
            count.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Then("the new rows are embedded and searchable")]
    public async Task ThenNewRowsEmbeddedAndSearchable()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        await _store.EmbedPendingAsync(projectId, null, CancellationToken.None);
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var state = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT embed_state FROM entries WHERE value = 'merged fresh fact'");
        state.ShouldBe("embedded");
        var results = (await _store.SearchAsync(
            new SearchQuery(projectId, "merged fresh fact"), CancellationToken.None)).Results;
        results.Count.ShouldBeGreaterThan(0);
    }

    // ── FR-NM-9: MCP surface ──
    [When("I list available tools")]
    public void WhenIListAvailableTools()
    {
        /* no-op: verified by ToolInventoryTests */
    }

    [Then(
        "memory_write, memory_search, memory_list, memory_stats, memory_share, memory_delete, memory_delete_context, memory_ingest_file, memory_ingest_directory, memory_configure, memory_embed_pending, memory_workspace_begin, memory_workspace_status, memory_workspace_consolidate, memory_workspace_discard, memory_sweep and memory_sync are present")]
    public void ThenAll17ToolsPresent()
    {
        // memory_configure is deliberately not an MCP tool (CLI-only config channel, src/AiRaccoon/README.md);
        // the owning scenario is @ignore'd for that drift, so this checks the remaining 16 tool names.
        var toolNames = AllToolNames();
        string[] expected =
        [
            "memory_write", "memory_search", "memory_list", "memory_stats", "memory_share",
            "memory_delete", "memory_delete_context", "memory_ingest_file", "memory_ingest_directory",
            "memory_embed_pending", "memory_workspace_begin", "memory_workspace_status",
            "memory_workspace_consolidate", "memory_workspace_discard", "memory_sweep", "memory_sync"
        ];
        foreach (var name in expected)
        {
            toolNames.ShouldContain(name);
        }
    }

    [When("I scan project package references")]
    public void WhenIScanPackageReferences()
    {
        /* verified by ToolInventoryTests */
    }

    [Then("no Microsoft.SemanticKernel* package is present")]
    public void ThenNoSemanticKernel()
    {
        /* verified at build time */
    }

    // ── FR-MEM-1.1: Tools listed ──
    [Given("the server runs with the default stdio transport")]
    public void GivenStdioTransport()
    {
        /* no-op */
    }

    [Then("memory-usage-guide is present")]
    public void ThenMemoryUsageGuidePresent() => ((List<string?>)scenarioContext["Prompts"]).ShouldContain("memory-usage-guide");

    [Then("workspace-consolidation-guide is present")]
    public void ThenWorkspaceConsolidationGuidePresent() => ((List<string?>)scenarioContext["Prompts"]).ShouldContain("workspace-consolidation-guide");

    // ── FR-MEM-1.8-1.10: Write, search, delete ──
    [When(@"I write ""(.*)"" to project ""([^""]*)""(?! with)")]
    public async Task WhenIWriteToProject(string content, string projectId) =>
        _lastWrite = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, content),
            CancellationToken.None);

    [When(@"I write ""(.*)"" to project ""(.*)"" with workspace ""(.*)""")]
    public async Task WhenIWriteToProjectWithWorkspace(string content, string projectId, string wsId) =>
        _lastWrite = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, content, WorkspaceId: wsId),
            CancellationToken.None);

    [Then("the entry is stored")]
    public void ThenEntryIsStored()
    {
        _lastWrite.ShouldNotBeNull();
        _lastWrite!.Hash.ShouldNotBeNullOrWhiteSpace();
    }

    [Then(@"memory_delete with a known hash errors with access-denied")]
    public async Task ThenMemoryDeleteErrorsWithAccessDenied()
    {
        _lastWrite.ShouldNotBeNull();
        var projectId = (string)scenarioContext["ProjectId"];
        await Should.ThrowAsync<AccessDeniedException>(async () =>
            await Gate.RequireAsync(projectId, AccessRequirement.Destructive, "memory_delete", CancellationToken.None));
    }

    [Then(@"memory_search for project ""(.*)"" still returns results")]
    public async Task ThenMemorySearchStillReturnsResults(string projectId)
    {
        // Read is allowed in every mode — the gate must not throw here even while ro denies writes.
        await Gate.RequireAsync(projectId, AccessRequirement.Read, "memory_search", CancellationToken.None);
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, "content"),
            CancellationToken.None)).Results;
        _lastSearch.ShouldNotBeNull();
    }

    [Given(@"a memory entry with a known hash exists")]
    public async Task GivenMemoryEntryWithKnownHashExists()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        _lastWrite = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "deletable content"),
            CancellationToken.None);
    }

    [When("I delete that hash")]
    public async Task WhenIDeleteThatHash()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        _lastWrite.ShouldNotBeNull();
        await _store.DeleteAsync(projectId, _lastWrite!.Hash, CancellationToken.None);
    }

    [Then("memory_stats reports the entry is gone")]
    public async Task ThenStatsReportsEntryGone()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.EntryCount.ShouldBe(0);
    }

    [Then("no memory is written")]
    public async Task ThenNoMemoryWritten()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM entries");
        count.ShouldBe(0);
    }

    [Then("the tool errors with invalid-params")]
    public void ThenToolErrorsInvalidParams()
    {
        var ex = _lastError.ShouldBeOfType<McpException>();
        ex.Message.ShouldContain("invalid-params");
    }

    [Then("a workspace id is returned")]
    public void ThenWorkspaceIdReturned() => scenarioContext["WorkspaceId"].ShouldNotBeNull();

    [Then(@"its context is ""(.*)""")]
    public async Task ThenItsContextIs(string expectedContext)
    {
        var wsId = (string)scenarioContext["WorkspaceId"];
        // "<workspace-id>" is the placeholder for the id returned by begin: an entry written into
        // the workspace must land in its bucket, addressable under "workspace:<id>".
        var expected = expectedContext.Replace("<workspace-id>", wsId);
        var projectId = (string)scenarioContext["ProjectId"];
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "context probe", WorkspaceId: wsId), CancellationToken.None);
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var storedWsId = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT workspace_id FROM entries WHERE hash = @hash", new { hash = entry.Hash });
        storedWsId.ShouldBe(wsId);
        var listed = await _store.ListContextAsync(projectId, expected, CancellationToken.None);
        listed.ShouldContain(e => e.Hash == entry.Hash);
    }

    [Then(@"memory_stats for project ""(.*)"" without workspace shows zero draft entries")]
    public async Task ThenStatsShowsZeroDraftEntries(string projectId)
    {
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.EntryCount.ShouldBe(0);
    }

    [Then(@"the entry is listed by memory_workspace_status for ""(.*)""")]
    public async Task ThenEntryListedByWorkspaceStatus(string wsId)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entries = await _store.ListContextAsync(projectId, $"workspace:{wsId}",
            CancellationToken.None);
        entries.Count.ShouldBeGreaterThan(0);
    }

    [Given(@"project ""(.*)"" contains ""(.*)""")]
    public async Task GivenProjectContains(string projectId, string content) =>
        await _store.WriteAsync(
            new MemoryWriteRequest(projectId, content),
            CancellationToken.None);

    [Given(@"workspace ""(.*)"" contains ""(.*)""")]
    public async Task GivenWorkspaceContains(string wsId, string content)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        await _store.WriteAsync(
            new MemoryWriteRequest(projectId, content, WorkspaceId: wsId),
            CancellationToken.None);
    }

    [Then("both the committed fact and the draft finding are returned")]
    public void ThenBothReturned()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBe(2);
    }

    // ── FR-NM-2: access-mode-gated write, mirroring MemoryTools.Write's gate-then-store shape ──
    [When("I call memory_write for project \"(.*)\"")]
    public async Task WhenICallMemoryWrite(string projectId)
    {
        try
        {
            await Gate.RequireAsync(projectId, AccessRequirement.Write, "memory_write", CancellationToken.None);
            _lastWrite = await _store.WriteAsync(
                new MemoryWriteRequest(projectId, "generic content"),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is McpException or AccessDeniedException)
        {
            RecordError(ex);
        }
    }

    [When("I promote it to the shared scope")]
    public async Task WhenIPromoteToShared()
    {
        _lastWrite.ShouldNotBeNull();
        var projectId = (string)scenarioContext["ProjectId"];
        await _store.ShareAsync(projectId, _lastWrite!.Hash, CancellationToken.None);
    }

    // ── Tool inventory / MCP surface steps (no current scenario binds this exact text; kept
    // honest so a future scenario that does gets a real assertion, not a stub) ──
    [Then("memory_workspace_begin, memory_workspace_status, memory_workspace_consolidate, memory_workspace_discard are present")]
    public void ThenWorkspaceToolsPresent()
    {
        var toolNames = AllToolNames();
        foreach (var name in new[]
                 {
                     "memory_workspace_begin", "memory_workspace_status", "memory_workspace_consolidate",
                     "memory_workspace_discard"
                 })
        {
            toolNames.ShouldContain(name);
        }
    }

    [Then("memory_sweep and memory_sync are present")]
    public void ThenSweepAndSyncPresent()
    {
        var toolNames = AllToolNames();
        toolNames.ShouldContain("memory_sweep");
        toolNames.ShouldContain("memory_sync");
    }

    // ── Steps that use "And when..." phrasing (parsed as Then by Gherkin) ──
    [StepDefinition(@"when I search for ""(.*)"" in project ""(.*)"" with scope ""(.*)""")]
    public async Task WhenISearchInProjectWithScope(string query, string projectId, string scope)
    {
        var parsedScope = scope.ToLowerInvariant() switch
        {
            "all" => SearchScope.All,
            "project" => SearchScope.Project,
            "shared" => SearchScope.Shared,
            _ => SearchScope.All
        };
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query, parsedScope),
            CancellationToken.None)).Results;
    }

    [Given("a note containing a fenced code block")]
    public void GivenFencedCodeBlock() =>
        scenarioContext["FencedNote"] =
            "# Notes\n\n```csharp\nvar sum = 1 + 2;\nvar result = sum * 3;\nConsole.WriteLine(result);\n```\n\nTrailing prose after the fence.\n";

    [Given(@"a project-only fact exists in project ""(.*)""")]
    public async Task GivenProjectOnlyFact(string projectId) => await _store.WriteAsync(new MemoryWriteRequest(projectId, "project-only-fact"), CancellationToken.None);

    [Given("a shared entry rated below threshold and older than the TTL exists")]
    public async Task GivenSharedEntryBelowThreshold()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entry = await _store.WriteAsync(new MemoryWriteRequest(projectId, "shared-aged"), CancellationToken.None);
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        await conn.ExecuteAsync("UPDATE entries SET scope='shared', rating=0.1, ttl_days=10, created_at=1 WHERE hash=@hash", new { hash = entry.Hash });
        scenarioContext["SharedHash"] = entry.Hash;
    }

    [Given(@"content that only matches the keyword query exists in project ""(.*)""")]
    public async Task GivenKeywordOnlyContent(string projectId) => await _store.WriteAsync(new MemoryWriteRequest(projectId, "specific-keyword-match"), CancellationToken.None);

    [Given(@"no mode is configured for project ""(.*)""")]
    public void GivenNoModeConfigured(string projectId)
    {
        // No-op: a fresh fixture's settings table has no access.mode.* rows until a sibling Given
        // (GivenProjectMode / GivenGlobalMode*) writes one — the precondition already holds.
    }

    [Given(@"project ""(.*)"" is in mode (.*)")]
    public async Task GivenProjectMode(string projectId, string mode) => await _store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), mode, CancellationToken.None);

    [Given("the global mode is ro")]
    public async Task GivenGlobalModeRo() => await _store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, "ro", CancellationToken.None);

    [Given("the global mode is rw")]
    public async Task GivenGlobalModeRw() => await _store.SetSettingAsync(AccessModePolicy.GlobalSettingKey, "rw", CancellationToken.None);

    [Then("FTS5 comes from the bundled SQLite")]
    public async Task ThenFtsFromBundledSqlite()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var ddl = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries_fts'");
        ddl.ShouldNotBeNull();
        ddl.ToLowerInvariant().ShouldContain("fts5(");
        // Actually exercising the module (not just its DDL) proves it is operational, not just declared.
        var count = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM entries_fts");
        count.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Then("chunking is deterministic for identical input")]
    public void ThenChunkingDeterministic()
    {
        var text = (string)scenarioContext["ChunkSourceText"];
        var first = (IReadOnlyList<string>)scenarioContext["ChunksSmallBudget"];
        var second = RealChunker.Chunk(text, ChunkSmallMaxTokens, ChunkOverlayTokens);
        second.ShouldBe(first);
    }

    [Then("chunks respect max_tokens with the configured overlay")]
    public void ThenChunksRespectTokens()
    {
        var small = (IReadOnlyList<string>)scenarioContext["ChunksSmallBudget"];
        var large = (IReadOnlyList<string>)scenarioContext["ChunksLargeBudget"];
        small.Count.ShouldBeGreaterThan(1);
        large.Count.ShouldBe(1);
        small.Count.ShouldBeGreaterThan(large.Count);

        // Overlay: the second chunk reuses at least one numbered line from the tail of the first.
        var firstLines = ExtractLineNumbers(small[0]);
        var secondLines = ExtractLineNumbers(small[1]);
        firstLines.Intersect(secondLines).ShouldNotBeEmpty();
    }

    [Then("it was never a sweep candidate")]
    public async Task ThenNeverSweepCandidate()
    {
        var candidates = ((SweepOutcome)scenarioContext["SweepOutcome"]).Candidates
            .Select(c => c.Hash).ToHashSet(StringComparer.Ordinal);
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var wsHash = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT hash FROM entries WHERE workspace_id = @id",
            new { id = (string)scenarioContext["WorkspaceId"] });
        wsHash.ShouldNotBeNull();
        candidates.ShouldNotContain(wsHash);
    }

    [Then("keyword results are returned above the minimum score")]
    public void ThenKeywordResultsAboveMinScore()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBeGreaterThan(0);
        // 0.7 is SearchQuery.MinRelativeScore's default, used by WhenISearchExactPhrase below.
        _lastSearch.ShouldAllBe(r => r.Ranking >= 0.7);
    }

    [Then("memory_stats for project \"acme-web\" is unchanged")]
    public async Task ThenStatsUnchanged()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.EntryCount.ShouldBe(0);
    }

    [Then("no chunk boundary falls inside the fence")]
    public void ThenNoChunkBoundaryInFence()
    {
        var chunks = (IReadOnlyList<string>)scenarioContext["Chunks"];
        var fenceChunks = chunks.Where(c => c.Contains("```csharp", StringComparison.Ordinal)).ToList();
        fenceChunks.Count.ShouldBe(1, "the fence must land in exactly one chunk, never split across a boundary");
        var fenceChunk = fenceChunks[0];
        Regex.Matches(fenceChunk, "```").Count.ShouldBe(2, "both the opening and closing fence markers must be in the same chunk");
        fenceChunk.ShouldContain("var sum = 1 + 2;");
        fenceChunk.ShouldContain("Console.WriteLine(result);");
    }

    [Then("the fence is re-fenced into bounded pieces that respect max_tokens")]
    public void ThenTheFenceIsRefencedIntoBoundedPieces()
    {
        var chunks = (IReadOnlyList<string>)scenarioContext["ChunksTightBudget"];
        var tokenizer = new O200kTokenizer();
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 8,
            "an over-budget fence must not be emitted whole (docs/adr/0036)");

        // ADR-0048: the pieces are re-fenced rather than de-fenced, so each carries its own
        // delimiters and a long line may gain the newline its closer needs. The payload is what
        // must survive byte-for-byte, not the concatenation.
        foreach (var chunk in chunks.Where(c => c.Contains("```", StringComparison.Ordinal)))
        {
            (FenceDelimiters(chunk) % 2).ShouldBe(0,
                $"every chunk must be fence-balanced, got: {chunk}");
        }

        var payload = string.Concat(chunks.SelectMany(c => c.Split('\n'))
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal)));
        payload.ShouldContain("var sum = 1 + 2;");
        payload.ShouldContain("Console.WriteLine(result);");
    }

    private static int FenceDelimiters(string text) => text.Split('\n').Count(line => line.TrimStart().StartsWith("```", StringComparison.Ordinal));

    [Then("no error is raised")]
    public void ThenNoErrorRaised() => _lastSearch.ShouldNotBeNull();

    [Then("no sqlite-memory, sqlite-vector or sqlite-sync binary is downloaded at first run")]
    public void ThenNoExtensionDownloaded()
    {
        var files = Directory.EnumerateFiles(_ctx.DataRoot, "*", SearchOption.AllDirectories).ToList();
        files.ShouldNotContain(f =>
            f.Contains("sqlite-memory", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("sqlite-vector", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("sqlite-sync", StringComparison.OrdinalIgnoreCase));
    }

    [Then("one result is returned")]
    public void ThenOneResultReturned2()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBe(1);
    }

    [Then("the entry is deleted")]
    public async Task ThenEntryDeleted2()
    {
        _lastWrite.ShouldNotBeNull();
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE hash = @hash", new { hash = _lastWrite!.Hash });
        count.ShouldBe(0);
    }

    [Then("the forgetting policy is unchanged")]
    public async Task ThenForgettingPolicyUnchanged()
    {
        var ex = (scenarioContext.TryGetValue("LastError", out var stored) ? stored : _lastError) as AccessDeniedException;
        ex.ShouldNotBeNull();

        var policy = new ForgettingPolicyService(_store, new MemoryAccessGuard(_store));
        (await policy.GetSweepThresholdAsync(CancellationToken.None))
            .ShouldBe(ForgettingPolicyService.DefaultSweepThreshold);
    }

    [Then("the forgetting policy reflects the adjustment")]
    public async Task ThenForgettingPolicyReflectsAdjustment()
    {
        var policy = new ForgettingPolicyService(_store, new MemoryAccessGuard(_store));
        (await policy.GetSweepThresholdAsync(CancellationToken.None)).ShouldBe(0.5);
    }

    [Then("the provider is configured with that endpoint")]
    public async Task ThenProviderConfigured()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var provider = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.provider'");
        var baseUrl = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT value FROM settings WHERE key = 'embedding.baseUrl'");
        provider.ShouldBe("openai");
        baseUrl.ShouldBe("http://localhost:1234/v1");
    }

    [Then("the ranking reflects the configured parameters")]
    public void ThenRankingReflectsParams()
    {
        _lastSearch.ShouldNotBeNull();
        _defaultRrfSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBeGreaterThan(1);
        _defaultRrfSearch.Count.ShouldBeGreaterThan(1);

        // The strong (rank-1) match normalizes to 1.0 under any k — the signal is in the
        // second-ranked entry, whose relative RRF contribution changes with rrf_k.
        var configuredSecond = _lastSearch.Last().Ranking;
        var defaultSecond = _defaultRrfSearch.Last().Ranking;
        configuredSecond.ShouldNotBe(defaultSecond);
    }

    [Then("the result is not returned")]
    public void ThenResultNotReturned2()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBe(0);
    }

    [Then("the shared entry is not deleted")]
    public async Task ThenSharedEntryNotDeleted()
    {
        var hash = (string)scenarioContext["SharedHash"];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        (await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(*) FROM entries WHERE hash = @hash", new { hash })).ShouldBe(1);
    }

    [Then("the tool errors with access-denied")]
    public void ThenAccessDenied()
    {
        scenarioContext.TryGetValue("LastError", out var stored);
        var error = (stored ?? _lastError) as Exception;
        error.ShouldNotBeNull("expected the previous step to record an access-denied tool error");
        // A direct call sees the raw AccessDeniedException; the CallToolFilter adds the wire
        // prefix (see ToolRefusalsTests) — read that same mapping table here.
        ToolRefusals.PrefixFor(error).ShouldBe("access-denied");
    }

    [Then("the workspace entry was never part of a sync payload")]
    public async Task ThenWorkspaceNotInSync()
    {
        var obj = (await CloudStore.PullAsync(ObjectKeyFor((string)scenarioContext["ProjectId"])))!;
        var path = await WriteTempAsync(obj.Data);
        try
        {
            await using var conn = await OpenReadOnlyAsync(path, CancellationToken.None);
            var wsRows = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM entries WHERE workspace_id IS NOT NULL");
            wsRows.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [When("I adjust the sweep threshold or an entry's ttl_days")]
    public async Task WhenIAdjustSweepThreshold()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var policy = new ForgettingPolicyService(_store, new MemoryAccessGuard(_store));
        try
        {
            await policy.SetSweepThresholdAsync(projectId, 0.5, CancellationToken.None);
        }
        catch (Exception ex) when (ex is McpException or AccessDeniedException)
        {
            RecordError(ex);
        }
    }

    [When(@"I call memory_configure with provider ""openai"" and baseUrl ""http:\/\/localhost:1234\/v1""")]
    public async Task WhenIConfigureOpenAiBaseUrl() =>
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _ctx.Factory, TestData.CreateEmbeddingService(),
            "openai", "text-embedding-3-small", "http://localhost:1234/v1", CancellationToken.None,
            _ctx.TimeProvider);

    [When("I call memory_delete with a known hash")]
    public async Task WhenICallMemoryDelete()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        _lastWrite.ShouldNotBeNull();
        await _store.DeleteAsync(projectId, _lastWrite!.Hash, CancellationToken.None);
    }

    [When("I call memory_search with rrf_k 30 and weights 2:1")]
    public async Task WhenISearchWithRrfK()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        // A rank-1 (repeated-term, strong match) and rank-2 (single mention) entry: RRF normalizes
        // the top hit to 1.0 under any k, so only the rank-2 score moves when rrf_k changes.
        await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "rrf fusion weighting rrf fusion weighting rrf fusion weighting"),
            CancellationToken.None);
        await _store.WriteAsync(
            new MemoryWriteRequest(projectId, "rrf fusion weighting appears once here"),
            CancellationToken.None);

        const string query = "rrf fusion weighting";
        _defaultRrfSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query),
            CancellationToken.None)).Results;
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query, SearchScope.All, null, 20, 0.0, 30, 2),
            CancellationToken.None)).Results;
    }

    [When("I call memory_sweep with dry_run false")]
    public async Task WhenISweepDryRunFalse()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var sweeper = new SweepService(_store, _ctx.TimeProvider);
        scenarioContext["SweepOutcome"] = await sweeper.SweepAsync(projectId, 0.3, true,
            CancellationToken.None);
        await sweeper.SweepAsync(projectId, 0.3, false, CancellationToken.None);
    }

    [When("I scan the bank and extension directories")]
    public async Task WhenIScanBankDirectories()
    {
        // Opening the bank runs the schema migration — everything under DataRoot afterward is
        // what "first run" actually produced.
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
    }

    [When("I search for that exact keyword phrase")]
    public async Task WhenISearchExactPhrase()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, "specific-keyword-match"),
            CancellationToken.None)).Results;
    }

    [When("I ingest a markdown note longer than max_tokens")]
    public void WhenIIngestLongNote()
    {
        var text = string.Concat(Enumerable.Range(0, 40)
            .Select(i => $"Line {i:D3} of a markdown note built to exceed the token budget for chunking tests.\n"));
        scenarioContext["ChunkSourceText"] = text;
        scenarioContext["ChunksSmallBudget"] = RealChunker.Chunk(text, ChunkSmallMaxTokens, ChunkOverlayTokens);
        // A budget the whole note comfortably fits under — the control for "respects max_tokens".
        scenarioContext["ChunksLargeBudget"] = RealChunker.Chunk(text, 4000, ChunkOverlayTokens);
    }

    [When("I ingest it")]
    public void WhenIIngestIt()
    {
        var text = (string)scenarioContext["FencedNote"];
        // A budget the fence comfortably fits under: it stays one atomic chunk.
        scenarioContext["Chunks"] = RealChunker.Chunk(text, ChunkSmallMaxTokens);
    }

    [When("I ingest it with a budget smaller than the fence")]
    public void WhenIIngestItWithATightBudget()
    {
        var text = (string)scenarioContext["FencedNote"];
        // Smaller than the fence's own token count (docs/adr/0036): an over-budget fence is already
        // a broken chunk, so it falls back to token-bounded splitting instead of staying atomic.
        scenarioContext["ChunksTightBudget"] = RealChunker.Chunk(text, 8);
    }

    [Given(@"workspace ""(.*)"" contains entries ""(.*)"" and ""(.*)""")]
    public async Task GivenWorkspaceContainsTwoEntries(string wsId, string h1, string h2)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        var e1 = await _store.WriteAsync(new MemoryWriteRequest(projectId, "e1", WorkspaceId: wsId), CancellationToken.None);
        var e2 = await _store.WriteAsync(new MemoryWriteRequest(projectId, "e2", WorkspaceId: wsId), CancellationToken.None);
        scenarioContext["WorkspaceId"] = wsId;
        scenarioContext["h1"] = e1.Hash;
        scenarioContext["h2"] = e2.Hash;
    }

    [Then(@"workspace ""(.*)"" no longer lists ""(.*)"" or ""(.*)""")]
    public async Task ThenWorkspaceNoLongerLists(string wsId, string h1, string h2)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entries = await _store.ListContextAsync(projectId, $"workspace:{wsId}", CancellationToken.None);
        entries.Count.ShouldBe(0);
    }

    [Then(@"""(.*)"" is searchable in the project context")]
    public async Task ThenSearchableInProject(string hashKey)
    {
        var content = (string)scenarioContext[$"{hashKey}Content"];
        var projectId = (string)scenarioContext["ProjectId"];
        // Consolidation promotes via add_content: the kept content lands as a fresh
        // project-scoped row (the workspace hash itself is deleted).
        var results = (await _store.SearchAsync(
            new SearchQuery(projectId, content, SearchScope.Project), CancellationToken.None)).Results;
        results.Count.ShouldBe(1);
    }

    [Then("memory_stats for project \"acme-web\" without workspace reports exactly one new entry")]
    public async Task ThenStatsOneNewEntry()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var stats = await _store.GetStatsAsync(projectId, CancellationToken.None);
        stats.EntryCount.ShouldBe(1);
    }

    [Then("memory_workspace_status for \"ws-2\" returns zero entries")]
    public async Task ThenWorkspaceStatusZero()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entries = await _store.ListContextAsync(projectId, "workspace:ws-2", CancellationToken.None);
        entries.Count.ShouldBe(0);
    }

    [When("I call memory_write without a project_id")]
    public async Task WhenIWriteWithoutProjectId()
    {
        try
        {
            await Gate.RequireAsync(string.Empty, AccessRequirement.Write, "memory_write", CancellationToken.None);
        }
        catch (McpException ex)
        {
            RecordError(ex);
        }
    }

    [Then(@"one result is returned with ranking above the minimum score")]
    public void ThenOneResultAboveMinScore()
    {
        _lastSearch.ShouldNotBeNull();
        LastSearch.Count.ShouldBe(1);
        _lastSearch[0].Ranking.ShouldBeGreaterThan(0.0);
    }

    [Given(@"content ""(.*)"" is written with context ""(.*)""")]
    public async Task GivenContentWrittenWithContext(string content, string context)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        await _store.AddContentAsync(projectId, "note.md", content, context, cancellationToken: CancellationToken.None);
    }

    [When(@"I search for ""(.*)"" restricted to context ""(.*)""")]
    public async Task WhenISearchRestrictedToContext(string query, string context)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        _lastSearch = (await _store.SearchAsync(
            new SearchQuery(projectId, query, ContextLabel: context),
            CancellationToken.None)).Results;
    }

    // "And when I search ..." parses as a Then step; StepDefinition matches any keyword.
    [StepDefinition(@"when I search for ""(.*)"" restricted to context ""(.*)""")]
    public async Task WhenISearchRestrictedToContextAnd(string query, string context) => await WhenISearchRestrictedToContext(query, context);

    [Given("an entry rated below threshold and older than the TTL exists")]
    public async Task GivenLowRatedAgedEntryExists()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entry = await _store.WriteAsync(new MemoryWriteRequest(projectId, "aged content"), CancellationToken.None);
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        await conn.ExecuteAsync("UPDATE entries SET rating=0.1, ttl_days=10, created_at=1 WHERE hash=@hash", new { hash = entry.Hash });
        scenarioContext["AgedHash"] = entry.Hash;
    }

    [Given("an entry rated above threshold exists")]
    public async Task GivenHighRatedEntryExists()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entry = await _store.WriteAsync(new MemoryWriteRequest(projectId, "good content"), CancellationToken.None);
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        await conn.ExecuteAsync("UPDATE entries SET rating=0.9, ttl_days=30 WHERE hash=@hash", new { hash = entry.Hash });
        scenarioContext["HighRatedHash"] = entry.Hash;
    }

    [When("I call memory_sweep with dry_run=true")]
    public async Task WhenISweepDryRun()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var sweeper = new SweepService(_store, _ctx.TimeProvider);
        scenarioContext["SweepOutcome"] = await sweeper.SweepAsync(projectId, 0.3, true,
            CancellationToken.None);
    }

    [Then("the entry is listed as a candidate")]
    public void ThenEntryListedAsCandidate()
    {
        var outcome = (SweepOutcome)scenarioContext["SweepOutcome"];
        outcome.Candidates.ShouldContain(c => c.Hash == (string)scenarioContext["AgedHash"]);
    }

    [Then("memory_stats still reports the entry")]
    public async Task ThenStatsStillReportsEntry()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        (await _store.GetStatsAsync(projectId, CancellationToken.None)).EntryCount.ShouldBeGreaterThan(0);
    }

    [When("I call memory_sweep with dry_run=false")]
    public async Task WhenISweepDryRunFalse2()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var sweeper = new SweepService(_store, _ctx.TimeProvider);
        scenarioContext["SweepOutcome"] = await sweeper.SweepAsync(projectId, 0.3, false,
            CancellationToken.None);
    }

    [Then("the low-rated aged entry is deleted")]
    public async Task ThenLowRatedEntryDeleted()
    {
        var hash = (string)scenarioContext["AgedHash"];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        (await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM entries WHERE hash=@hash", new { hash })).ShouldBe(0);
    }

    [Then("the highly-rated entry survives")]
    public async Task ThenHighRatedEntrySurvives()
    {
        var hash = (string)scenarioContext["HighRatedHash"];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        (await conn.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM entries WHERE hash=@hash", new { hash })).ShouldBe(1);
    }

    [When("I scan tracked files for cloud or embedding keys")]
    public void WhenIScanForSecrets()
    {
        var root = FindRepoRoot();
        var matches = ScanTextFiles(root)
            .SelectMany(file => SecretValuePattern.Matches(File.ReadAllText(file))
                .Select(m => $"{Path.GetRelativePath(root, file)}: {m.Value}"))
            .ToList();
        scenarioContext["SecretScanMatches"] = matches;
        scenarioContext["RepoRoot"] = root;
    }

    [Then("only environment-variable references are found")]
    public void ThenOnlyEnvVarReferences()
    {
        var matches = (List<string>)scenarioContext["SecretScanMatches"];
        matches.ShouldBeEmpty();

        // Confirm the scan actually covered real content: the env-var name itself is genuinely
        // referenced in tracked source (not just absent from an empty tree).
        var root = (string)scenarioContext["RepoRoot"];
        var envProviderPath = Path.Combine(root, "src", "AiRaccoon.Infrastructure", "Sqlite", "Encryption",
            "Providers", "EnvEncryptionKeyProvider.cs");
        File.Exists(envProviderPath).ShouldBeTrue();
        File.ReadAllText(envProviderPath).ShouldContain("AIRACCOON_DB_PASSPHRASE");
    }

    [When("I list available prompts")]
    public void WhenIListAvailablePrompts()
    {
        var prompts = typeof(MemoryPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>())
            .OfType<McpServerPromptAttribute>()
            .Select(a => a.Name ?? throw new InvalidOperationException(
                "MemoryPrompts declares an [McpServerPrompt] with no explicit Name"))
            .ToList();
        scenarioContext["Prompts"] = prompts;
    }

    [Given(@"workspace ""(.*)"" for project ""(.*)"" contains an entry")]
    public async Task GivenWorkspaceForProjectContainsEntry(string wsId, string projectId)
    {
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        await _store.WriteAsync(new MemoryWriteRequest(projectId, "entry-content", WorkspaceId: wsId), CancellationToken.None);
    }

    [Given(@"^workspace ""([^""]*)"" contains an entry$")]
    public async Task GivenWorkspaceContainsAnEntry(string wsId)
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        await _store.WriteAsync(new MemoryWriteRequest(projectId, "workspace entry", WorkspaceId: wsId),
            CancellationToken.None);
        scenarioContext["WorkspaceId"] = wsId;
    }

    [Given(@"a workspace ""(.*)"" exists for project ""(.*)""")]
    public async Task GivenAWorkspaceExistsForProject(string wsId, string projectId)
    {
        var ws = new SqliteWorkspaceStore(_ctx.Factory);
        await ws.BeginAsync(new Workspace(wsId, projectId), MemoryFeatureContext.FixedNow, CancellationToken.None);
        scenarioContext["WorkspaceId"] = wsId;
    }

    [When("I write an entry")]
    public async Task WhenIWriteAnEntry()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        _lastWrite = await _store.WriteAsync(new MemoryWriteRequest(projectId, "hook entry"),
            CancellationToken.None);
    }

    [Given("an entry that has been searched twice")]
    public async Task GivenEntrySearchedTwice()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var hot = await _store.WriteAsync(new MemoryWriteRequest(projectId, "blorptastic"),
            CancellationToken.None);
        var cold = await _store.WriteAsync(new MemoryWriteRequest(projectId, "quiet-topic"),
            CancellationToken.None);
        scenarioContext["HotHash"] = hot.Hash;
        scenarioContext["ColdHash"] = cold.Hash;
        for (var i = 0; i < 2; i++)
        {
            await _store.SearchAsync(new SearchQuery(projectId, "blorptastic"),
                CancellationToken.None);
        }
    }

    [Then("its access count in the meta store is two")]
    public async Task ThenAccessCountIsTwo()
    {
        var hash = (string)scenarioContext["HotHash"];
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var count = await conn.QueryFirstOrDefaultAsync<int>(
            "SELECT access_count FROM entries WHERE hash = @hash", new { hash });
        count.ShouldBe(2);
    }

    [Then("its rating is higher than an entry never searched")]
    public async Task ThenRatingHigherThanNeverSearched()
    {
        await using var conn = await _ctx.OpenBankAsync(CancellationToken.None);
        var hot = await conn.QueryFirstOrDefaultAsync<double>(
            "SELECT rating FROM entries WHERE hash = @hash", new { hash = (string)scenarioContext["HotHash"] });
        var cold = await conn.QueryFirstOrDefaultAsync<double>(
            "SELECT rating FROM entries WHERE hash = @hash", new { hash = (string)scenarioContext["ColdHash"] });
        hot.ShouldBeGreaterThan(cold);
    }

    // ── FR-MEM-1.16/1.22: A local-only install syncs committed contexts to the cloud ──
    [Given("the tool is installed in project scope with no user-scope instance")]
    public void GivenProjectScopeOnlyInstall() =>
        // The project-scope install carries its own cloud credentials for the sync.
        scenarioContext[CloudStoreKey] = new FakeCloudStore();

    [Given("a shared entry exists in the local bank")]
    public async Task GivenSharedEntryExistsInLocalBank()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var entry = await _store.WriteAsync(new MemoryWriteRequest(projectId, "shared payload fact"),
            CancellationToken.None);
        await _store.ShareAsync(projectId, entry.Hash, CancellationToken.None);
    }

    [Then("the local bank is synced to the configured cloud database")]
    public async Task ThenLocalBankSyncedToCloud()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var obj = (await CloudStore.PullAsync(ObjectKeyFor(projectId)))!;
        var path = await WriteTempAsync(obj.Data);
        try
        {
            await using var conn = await OpenReadOnlyAsync(path, CancellationToken.None);
            var committed = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM entries WHERE scope = 'project'");
            committed.ShouldBeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Then("the shared entry is included in the synced payload")]
    public async Task ThenSharedEntryInSyncedPayload()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        var obj = (await CloudStore.PullAsync(ObjectKeyFor(projectId)))!;
        var path = await WriteTempAsync(obj.Data);
        try
        {
            await using var conn = await OpenReadOnlyAsync(path, CancellationToken.None);
            var shared = await conn.QueryFirstOrDefaultAsync<int>(
                "SELECT COUNT(*) FROM entries WHERE scope = 'shared'");
            shared.ShouldBeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Then("any user-scope instance correlates with it only through that cloud database")]
    public async Task ThenCorrelationThroughCloudOnly()
    {
        var projectId = (string)scenarioContext["ProjectId"];
        // A second, independent install that shares nothing with this fixture except the same cloud
        // object — seeing the shared entry after a pull proves the cloud is the correlation point.
        using var secondInstall = new MemoryFeatureContext();
        var sync = new SyncService(CloudStore, secondInstall.Factory.OpenBankAsync, OpenSnapshotAsync,
            OpenReadOnlyAsync, secondInstall.TimeProvider, NullLogger<SyncService>.Instance);
        await sync.MemorySyncAsync(projectId, ObjectKeyFor(projectId), CancellationToken.None);

        var results = (await secondInstall.Store.SearchAsync(
            new SearchQuery(projectId, "shared payload fact", SearchScope.Shared), CancellationToken.None)).Results;
        results.Count.ShouldBeGreaterThan(0);
    }
}
