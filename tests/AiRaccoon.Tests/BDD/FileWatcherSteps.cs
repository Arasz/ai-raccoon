using System.Globalization;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tools;
using Dapper;
using ModelContextProtocol;
using Reqnroll;
using Shouldly;

namespace AiRaccoon.Tests.BDD;

/// <summary>
///     Bindings for docs/features/file-watcher/file-watcher.feature; test wiring is documented in
///     docs/plans/file-watcher-implementation.md.
/// </summary>
[Binding]
public sealed class FileWatcherSteps(ScenarioContext scenarioContext)
{
    private const string DefaultProject = "proj-a";

    /// <summary>Real path → latest content token (assertion payload for "the file").</summary>
    private readonly Dictionary<string, string> _contentByPath = new(StringComparer.Ordinal);

    /// <summary>Real path → every content token ever written (for "no intermediate save ever searchable").</summary>
    private readonly Dictionary<string, List<string>> _contentsByPath = new(StringComparer.Ordinal);

    private readonly ScenarioContext _scenarioContext = scenarioContext;

    /// <summary>(project, path, token) triples for multi-file assertions.</summary>
    private readonly List<(string Project, string Path, string Token)> _searchables = [];

    private IReadOnlyList<MemorySearchResult>? _beforeSearch;

    /// <summary>
    ///     Resolved lazily: Reqnroll instantiates binding classes before BeforeScenario hooks run,
    ///     so an eager resolve would auto-construct a second context and break Hooks' registration.
    /// </summary>
    private FileWatcherFeatureContext? _ctx;

    private string? _displacedContent;
    private string? _lastCliError;
    private string? _lastCliMessage;

    private Exception? _lastError;
    private string? _lastFile;
    private string? _lastFileContent;
    private WatchTools.WatchStatusResult? _lastStatusDataData;
    private string? _removedPath;

    /// <summary>True once a scope was configured explicitly (the add step may then create a missing target dir).</summary>
    private bool _scopeExplicitlySet;

    private int _tokenSeq;

    private FileWatcherFeatureContext Ctx => _ctx ??= _scenarioContext.ScenarioContainer.Resolve<FileWatcherFeatureContext>();

    private static bool IsUnreadableSupported => !OperatingSystem.IsWindows();

    // ── Helpers ──

    /// <summary>
    ///     Runs a real CLI config command in-process against the scenario's store (same dispatch as
    ///     Program.cs), capturing combined output in _lastCliMessage and stderr in _lastCliError on
    ///     non-zero exit.
    /// </summary>
    private async Task<string> RunCliAsync(params string[] args)
    {
        CliArgs.TryParse(args, out var parsed);
        if (parsed!.Errors.Count > 0 || parsed.CommandPath.Length == 0)
        {
            throw new InvalidOperationException(
                $"CLI command did not parse: {string.Join("; ", parsed.Errors)}");
        }

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await TestData.CreateConfigCommands(Ctx.Store, settings: new SettingsCommands(), sync: new SyncCommands(),
                watch: new WatchCommands(Ctx.WatchStore))
            .RunAsync(parsed, new StandardStreams(TextReader.Null, stdout, stderr), CancellationToken.None);
        _lastCliMessage = stdout.ToString() + stderr.ToString();
        if (exit != 0)
        {
            _lastCliError = (stderr.ToString() + string.Join("; ", parsed.Errors)).Trim();
        }

        return _lastCliMessage;
    }

    private string NextToken(string seed) => $"zephyr{seed.Replace("/", string.Empty).Replace(".", string.Empty)}{_tokenSeq++}";

    private string Map(string virtualPath) => Ctx.MapPath(virtualPath);

    /// <summary>Resolves a step path: virtual paths start with '/', bare names are relative to the watched repo dir.</summary>
    private string Resolve(string pathOrName) => pathOrName.StartsWith('/') ? Map(pathOrName) : Path.Combine(Ctx.RepoDir, pathOrName);

    private string Normalized(string path) => IngestPath.Normalize(path);

    private async Task<List<MemorySearchResult>> SearchAsync(string projectId, string token) => [.. await Ctx.SearchAsync(projectId, token)];

    /// <summary>
    ///     Feature baseline: "the server" is configured for watching (enabled, whole bank in
    ///     scope) unless a scenario explicitly configured it — the feature's implicit server state.
    /// </summary>
    private async Task EnsureBaselineAsync()
    {
        if (await Ctx.Store.GetSettingAsync(WatchConfigKeys.EnabledGlobal) is null)
        {
            await Ctx.SetWatchEnabledGlobalAsync(true);
            await Ctx.SetWatchScopeGlobalAsync([Ctx.DataRoot]);
        }
    }

    /// <summary>Registers a watch through the real service (bypasses the agent access guard — this is background setup).</summary>
    private async Task SetupWatchAsync(string projectId, string virtualPath)
    {
        var path = Map(virtualPath);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        await Ctx.SetWatchEnabledGlobalAsync(true);
        await Ctx.AddWatchScopeGlobalAsync(path);
        _scopeExplicitlySet = true;
        await Ctx.Service.AddAsync(projectId, path);
        await Ctx.ReconcileOnceAsync();
    }

    private async Task SetupWatchWithIngestedAsync(string projectId, string virtualPath, IEnumerable<string> fileNames)
    {
        foreach (var name in fileNames)
        {
            WriteFileAt(virtualPath, name);
        }

        await SetupWatchAsync(projectId, virtualPath);
        foreach (var name in fileNames)
        {
            var path = Path.Combine(Map(virtualPath), name);
            (await EnsureSearchableAsync(projectId, _contentByPath[path], path)).ShouldBeTrue(
                $"'{name}' did not become searchable during watch setup");
        }
    }

    private void WriteFileAt(string virtualDir, string name)
    {
        var path = Path.Combine(Map(virtualDir), name);
        var token = NextToken(name);
        Ctx.WriteFile(path, $"{token} content");
        _contentByPath[path] = token;
        if (!_contentsByPath.TryGetValue(path, out var list))
        {
            list = [];
            _contentsByPath[path] = list;
        }

        list.Add(token);
        _lastFile = path;
        _lastFileContent = token;
    }

    /// <summary>Reconcile (idempotent) + poll until the token is searchable, optionally under a specific source file.</summary>
    private async Task<bool> EnsureSearchableAsync(string projectId, string token, string? sourcePath = null,
        int maxFakeSeconds = 2)
    {
        await Ctx.ReconcileOnceAsync();
        var normalized = sourcePath is null ? null : Normalized(sourcePath);
        return await Ctx.StepUntilAsync(async () =>
        {
            var results = await SearchAsync(projectId, token);
            return normalized is null ? results.Count > 0 : results.Any(r => Normalized(r.SourceFile ?? string.Empty) == normalized);
        }, maxFakeSeconds);
    }

    private async Task<bool> EnsureNotSearchableAsync(string projectId, string token, int maxFakeSeconds = 2)
    {
        await Ctx.ReconcileOnceAsync();
        return await Ctx.StepUntilAsync(async () =>
            (await SearchAsync(projectId, token)).Count == 0, maxFakeSeconds);
    }

    private async Task<WatchTools.WatchStatusResult> StatusAsync(string projectId) => _lastStatusDataData = (await Ctx.Tools.Status(projectId)).Data!;

    private void MakeUnreadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.None);
    }

    private void MakeReadable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Writes new content while readable, then locks the file: queues a Changed event whose digest fails.</summary>
    private void QueueUnreadableChange(string path)
    {
        MakeReadable(path);
        Ctx.WriteFile(path, $"{NextToken(Path.GetFileName(path))} locked");
        MakeUnreadable(path);
    }

    /// <summary>Drives N consecutive failing digests with exponential-backoff time advances.</summary>
    private async Task DriveFailuresAsync(string filePath, int count, bool firstQueued)
    {
        if (firstQueued)
        {
            // The first event may be watcher-delivered (written after setup), so give the OS
            // time to deliver it before the first tick; scan-queued events are unaffected.
            await Task.Delay(150);
        }

        for (var k = 1; k <= count; k++)
        {
            if (!firstQueued || k > 1)
            {
                QueueUnreadableChange(filePath);
                await Task.Delay(150);
            }

            if (k > 1)
            {
                Ctx.TimeProvider.Advance(TimeSpan.FromSeconds(1L << k - 2));
            }

            await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
        }
    }

    private async Task<int> CountEntriesAsync(string path, string? valueContains)
    {
        await using var connection = await Ctx.OpenBankAsync();
        var sql = valueContains is null
            ? "SELECT count(*) FROM entries WHERE source_file = @path"
            : "SELECT count(*) FROM entries WHERE source_file = @path AND value LIKE @value";
        object parameters = valueContains is null
            ? new { path = Normalized(path) }
            : new { path = Normalized(path), value = $"%{valueContains}%" };
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters));
    }

    private static string BrokenFile(string repoDir) => Path.Combine(repoDir, "broken.md");

    // ── Rule: Watching is opt-in and bounded by a scope allowlist ──
    [Given("^the server with watching disabled$")]
    public Task GivenServerWatchingDisabled() => Ctx.SetWatchEnabledGlobalAsync(false);

    [Given("^a scope allowlist containing \"([^\"]*)\"$")]
    public async Task GivenScopeAllowlist(string path)
    {
        var real = Map(path);
        Directory.CreateDirectory(real);
        await Ctx.SetWatchScopeGlobalAsync([real]);
        _scopeExplicitlySet = true;
    }

    [Given("^watching enabled$")]
    public Task GivenWatchingEnabled() => Ctx.SetWatchEnabledGlobalAsync(true);

    // ── Rule: Watch configuration is CLI-only and user-facing (real CLI via RunCliAsync) ──
    [Given("^watch enable \\* true$")]
    [When("^the user runs watch enable \\* true$")]
    public Task WhenUserRunsWatchEnable() => RunCliAsync("watch", "enable", "*", "true");

    [When("^the user runs watch scope add \\* ([^ ]*)$")]
    public Task WhenUserRunsWatchScopeAdd(string path) => RunCliAsync("watch", "scope", "add", "*", Map(path));

    [When("^the user runs watch disable ([^ ]*) (true|false)$")]
    public Task WhenUserRunsWatchDisable(string projectId, string enabled) => RunCliAsync("watch", "disable", projectId, enabled);

    [When("^the user runs watch concurrency ([^ ]*) ([0-9]+)$")]
    public Task WhenUserRunsWatchConcurrency(string projectId, int value) => RunCliAsync("watch", "concurrency", projectId, value.ToString(CultureInfo.InvariantCulture));

    [When("^the user runs watch registered$")]
    public Task WhenUserRunsWatchRegistered() => RunCliAsync("watch", "registered");

    [Then("^the CLI output lists the registered watch for \\\"([^\\\"]*)\\\" at \\\"([^\\\"]*)\\\"$")]
    public void ThenCliOutputListsRegisteredWatch(string projectId, string virtualPath)
    {
        _lastCliMessage.ShouldNotBeNull().ShouldContain($"project: {projectId}  path: {Map(virtualPath)}  registered: ");
        _lastCliMessage.ShouldContain(" lastChange: never");
    }

    [Given("^watching enabled with scope \"([^\"]*)\"$")]
    public async Task GivenWatchingEnabledWithScope(string path)
    {
        var real = Map(path);
        Directory.CreateDirectory(real);
        await Ctx.SetWatchEnabledGlobalAsync(true);
        await Ctx.SetWatchScopeGlobalAsync([real]);
        _scopeExplicitlySet = true;
    }

    [When("^the server restarts$")]
    public void WhenServerRestarts() => Ctx.Restart();

    [Then("^the command returns a message to add at least one scope$")]
    public void ThenCommandReturnsAddScopeMessage() => _lastCliMessage.ShouldNotBeNull().ShouldContain("add at least one scope");

    [Then("^the allowlist contains \"([^\"]*)\" and \"([^\"]*)\"$")]
    public async Task ThenAllowlistContains(string path1, string path2)
    {
        var config = await Ctx.ResolveConfigAsync(DefaultProject);
        config.Scope.Select(Normalized).ShouldContain(Normalized(Map(path1)));
        config.Scope.Select(Normalized).ShouldContain(Normalized(Map(path2)));
    }

    [Then("^watching stays disabled for \"([^\"]*)\"$")]
    public async Task ThenWatchingStaysDisabled(string projectId) => (await Ctx.ResolveConfigAsync(projectId)).Enabled.ShouldBeFalse();

    [Then("^watching stays enabled for other projects$")]
    public async Task ThenWatchingStaysEnabledForOthers() => (await Ctx.ResolveConfigAsync("proj-b")).Enabled.ShouldBeTrue();

    [Then("^the concurrency limit for \"([^\"]*)\" is ([0-9]+)$")]
    public async Task ThenConcurrencyLimit(string projectId, int expected) => (await Ctx.ResolveConfigAsync(projectId)).Concurrency.ShouldBe(expected);

    [Then("^the command errors with invalid-value$")]
    public void ThenCommandErrorsInvalidValue() => _lastCliError.ShouldNotBeNull().ShouldContain("invalid-value");

    [Then("^watching is still enabled with scope \"([^\"]*)\"$")]
    public async Task ThenWatchingStillEnabledWithScope(string path)
    {
        var config = await Ctx.ResolveConfigAsync(DefaultProject);
        config.Enabled.ShouldBeTrue();
        config.Scope.Select(Normalized).ShouldContain(Normalized(Map(path)));
    }

    // ── Rule: Watches are project-scoped ──
    [Given("^a project \"([^\"]*)\" and a project \"([^\"]*)\"$")]
    public async Task GivenTwoProjects(string p1, string p2)
    {
        Directory.CreateDirectory(Ctx.RepoDir);
        await Ctx.SetWatchEnabledGlobalAsync(true);
        await Ctx.SetWatchScopeGlobalAsync([Ctx.DataRoot]);
        _scopeExplicitlySet = true;
    }

    [Given("^a file \"([^\"]*)\" under the watched path$")]
    public void GivenFileUnderWatchedPath(string name) => WriteFileAt("/repo", name);

    // ── Rule: Watches support both files and directories ──
    [Given("^a file \"([^\"]*)\"$")]
    public void GivenAFile(string path)
    {
        var real = Map(path);
        Directory.CreateDirectory(Path.GetDirectoryName(real)!);
        var token = NextToken(Path.GetFileName(real));
        Ctx.WriteFile(real, $"{token} content");
        _contentByPath[real] = token;
        _lastFile = real;
        _lastFileContent = token;
    }

    [Given("^a directory \"([^\"]*)\" containing \"([^\"]*)\"$")]
    public void GivenDirectoryContaining(string path, string filePath)
    {
        Directory.CreateDirectory(Map(path));
        WriteFileAt(path, filePath);
    }

    [Given("^a directory \"([^\"]*)\" with files$")]
    public void GivenDirectoryWithFiles(string path)
    {
        Directory.CreateDirectory(Map(path));
        WriteFileAt(path, "alpha.md");
        WriteFileAt(path, "beta.md");
        WriteFileAt(path, "gamma.md");
    }

    [Given("^a directory \"([^\"]*)\" with many files$")]
    public void GivenDirectoryWithManyFiles(string path)
    {
        var dir = Map(path);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < 300; i++)
        {
            Ctx.WriteFile(Path.Combine(dir, $"f{i:D3}.md"), $"zephyrword{i} unique");
        }
    }

    [Given("^a directory \"([^\"]*)\" containing an unreadable file$")]
    public void GivenDirectoryWithUnreadableFile(string path)
    {
        Directory.CreateDirectory(Map(path));
        var file = Path.Combine(Map(path), "secret.md");
        Ctx.WriteFile(file, "zephyrsecret hidden");
        if (IsUnreadableSupported)
        {
            MakeUnreadable(file);
        }
    }

    // ── Rule: The initial scan is asynchronous ──
    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\"$")]
    public Task GivenWatchOnPath(string projectId, string path) => SetupWatchAsync(projectId, path);

    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" with \"([^\"]*)\" ingested$")]
    public Task GivenWatchWithFileIngested(string projectId, string path, string file) => SetupWatchWithIngestedAsync(projectId, path, [file]);

    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" with \"([^\"]*)\" and \"([^\"]*)\" ingested$")]
    public Task GivenWatchWithTwoFilesIngested(string projectId, string path, string file1, string file2) => SetupWatchWithIngestedAsync(projectId, path, [file1, file2]);

    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" that is still scanning$")]
    public async Task GivenWatchStillScanning(string projectId, string path)
    {
        WriteFileAt(path, "old.md");
        await SetupWatchAsync(projectId, path);
    }

    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" that is scanning$")]
    public Task GivenWatchScanning(string projectId, string path) => SetupWatchAsync(projectId, path);

    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" with no pending changes$")]
    public Task GivenWatchNoPendingChanges(string projectId, string path) => SetupWatchAsync(projectId, path);

    [Given("^a file whose digest is in flight$")]
    public void GivenFileDigestInFlight()
    {
        WriteFileAt("/repo", "file.md");
        _lastFileContent = _contentByPath[Path.Combine(Ctx.RepoDir, "file.md")];
    }

    [Given("^\"([^\"]*)\" changed while the server was stopped$")]
    public async Task GivenFileChangedWhileStopped(string file)
    {
        Ctx.EventSource.StopAll();
        Ctx.TimeProvider.Advance(TimeSpan.FromMinutes(5));
        WriteFileAt("/repo", file);
    }

    [Given("^\"([^\"]*)\" changed and queued for digest$")]
    public void GivenFileChangedQueuedForDigest(string file)
    {
        WriteFileAt("/repo", file);
        _lastFileContent = _contentByPath[Path.Combine(Ctx.RepoDir, file)];
    }

    [Given("^a change that lands just before a tick$")]
    public async Task GivenChangeJustBeforeTick()
    {
        WriteFileAt("/repo", "file.md");
        _lastFileContent = _contentByPath[Path.Combine(Ctx.RepoDir, "file.md")];
        await Task.Delay(150);
    }

    // ── Rule: All four filesystem event types trigger processing ──
    [Given("^watches for \"([^\"]*)\", \"([^\"]*)\" and \"([^\"]*)\" on three directories$")]
    public async Task GivenWatchesOnThreeDirectories(string p1, string p2, string p3)
    {
        await SetupWatchAsync(p1, "/repo-a");
        await SetupWatchAsync(p2, "/repo-b");
        await SetupWatchAsync(p3, "/repo-c");
    }

    [Given("^ten files changed across the three watches$")]
    public void GivenTenFilesChanged()
    {
        var plan = new[] { ("proj-a", "/repo-a", 4), ("proj-b", "/repo-b", 3), ("proj-c", "/repo-c", 3) };
        var seq = 0;
        foreach (var (project, dir, count) in plan)
        {
            for (var i = 0; i < count; i++)
            {
                var path = Path.Combine(Map(dir), $"f{seq}.md");
                var token = NextToken($"f{seq}");
                Ctx.WriteFile(path, $"{token} content");
                _searchables.Add((project, path, token));
                seq++;
            }
        }
    }

    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" with four pending files$")]
    public async Task GivenWatchWithFourPendingFiles(string projectId, string path)
    {
        await SetupWatchAsync(projectId, path);
        for (var i = 0; i < 4; i++)
        {
            var name = $"p{i}.md";
            WriteFileAt(path, name);
            _searchables.Add((projectId, Path.Combine(Map(path), name),
                _contentByPath[Path.Combine(Map(path), name)]));
        }
    }

    [Given("^a watch on a directory with many changes$")]
    public async Task GivenWatchOnDirWithManyChanges()
    {
        await SetupWatchAsync("proj-a", "/repo-a");
        for (var i = 0; i < 20; i++)
        {
            WriteFileAt("/repo-a", $"big{i}.md");
        }
    }

    [Given("^a watch on a directory with one change$")]
    public async Task GivenWatchOnDirWithOneChange()
    {
        await SetupWatchAsync("proj-b", "/repo-b");
        WriteFileAt("/repo-b", "small.md");
        _searchables.Add(("proj-b", Path.Combine(Map("/repo-b"), "small.md"),
            _contentByPath[Path.Combine(Map("/repo-b"), "small.md")]));
    }

    // ── Rule: Watch operation failures never fail the MCP server ──
    [Given("^a watch for \"([^\"]*)\" on path \"([^\"]*)\" where one file's digest fails$")]
    public async Task GivenWatchWhereDigestFails(string projectId, string path)
    {
        await SetupWatchAsync(projectId, path);
        var file = Path.Combine(Map(path), "broken.md");
        Ctx.WriteFile(file, "zephyrbroken content");
        if (IsUnreadableSupported)
        {
            MakeUnreadable(file);
        }
    }

    [Given("^a watch for \"([^\"]*)\" on a path that becomes unreadable$")]
    public async Task GivenWatchOnPathThatBecomesUnreadable(string projectId)
    {
        await SetupWatchAsync(projectId, "/repo");
        WriteFileAt("/repo", "file.md");
        if (IsUnreadableSupported)
        {
            MakeUnreadable(Path.Combine(Ctx.RepoDir, "file.md"));
        }
    }

    // ── Rule: Watch errors are reported through memory_watch_status ──
    [Given("^a watch for \"([^\"]*)\" whose file fails to ingest$")]
    public Task GivenWatchWhoseFileFailsToIngest(string projectId) => GivenWatchWhereDigestFails(projectId, "/repo");

    [Given("^a watch for \"([^\"]*)\" with a failed digest$")]
    public async Task GivenWatchWithFailedDigest(string projectId)
    {
        await SetupWatchAsync(projectId, "/repo");
        var file = BrokenFile(Ctx.RepoDir);
        Ctx.WriteFile(file, "zephyrbroken content");
        if (IsUnreadableSupported)
        {
            MakeUnreadable(file);
            await DriveFailuresAsync(file, 1, true);
        }
    }

    [Given("^a watch for \"([^\"]*)\" with an error$")]
    public Task GivenWatchWithError(string projectId) => GivenWatchWithFailedDigest(projectId);

    // ── Rule: A failing watch retries, then stops checking but keeps its status ──
    [Given("^a watch for \"([^\"]*)\" whose digests fail$")]
    public async Task GivenWatchWhoseDigestsFail(string projectId)
    {
        await SetupWatchAsync(projectId, "/repo");
        var file = BrokenFile(Ctx.RepoDir);
        Ctx.WriteFile(file, "zephyrbroken content");
        if (IsUnreadableSupported)
        {
            MakeUnreadable(file);
        }
    }

    [Given("^a watch for \"([^\"]*)\" with four failed digests$")]
    public async Task GivenWatchWithFourFailedDigests(string projectId)
    {
        await GivenWatchWhoseDigestsFail(projectId);
        if (IsUnreadableSupported)
        {
            await DriveFailuresAsync(BrokenFile(Ctx.RepoDir), 4, true);
        }
    }

    // ── Rule: memory_watch_remove on a non-existent watch is a no-op ──
    [Given("^a watch for \"([^\"]*)\" that was already removed$")]
    public async Task GivenWatchAlreadyRemoved(string projectId)
    {
        await SetupWatchAsync(projectId, "/repo");
        await Ctx.Service.RemoveAsync(projectId, Ctx.RepoDir);
        await Ctx.ReconcileOnceAsync();
        _removedPath = Ctx.RepoDir;
    }

    [Given("^a removed watch with a queued change$")]
    public async Task GivenRemovedWatchWithQueuedChange()
    {
        await SetupWatchAsync(DefaultProject, "/repo");
        WriteFileAt("/repo", "file.md");
        await Task.Delay(150);
        await Ctx.Service.RemoveAsync(DefaultProject, Ctx.RepoDir);
        await Ctx.ReconcileOnceAsync();
        _removedPath = Ctx.RepoDir;
    }

    // ── Rule: The watcher mirrors regardless of access tier ──
    [Given("^a project \"([^\"]*)\" with access tier (ro|rw)$")]
    public Task GivenProjectWithAccessTier(string projectId, string tier) => Ctx.SetAccessTierAsync(projectId, tier);

    [Given("^a project \"([^\"]*)\" with no watches$")]
    public void GivenProjectWithNoWatches(string projectId)
    {
        /* no registrations — status must return an empty list */
    }

    // ── Whens ──
    [When("^I call memory_watch_add for \"([^\"]*)\" on path \"([^\"]*)\"$")]
    public async Task WhenCallWatchAdd(string projectId, string path)
    {
        _lastError = null;
        await EnsureBaselineAsync();
        var target = Map(path);
        if (!File.Exists(target) && !Directory.Exists(target) && _scopeExplicitlySet)
        {
            Directory.CreateDirectory(target);
        }

        try
        {
            await Ctx.Tools.Add(projectId, target);
        }
        catch (Exception ex)
        {
            _lastError = ex;
            // Bridges to NativeMemorySteps.ThenAccessDenied, the shared "the tool errors with
            // access-denied" binding for the ro-agent-add scenario below.
            _scenarioContext["LastError"] = ex;
        }
    }

    [When("^I call memory_watch_add for \"([^\"]*)\" on path \"([^\"]*)\" twice$")]
    public async Task WhenCallWatchAddTwice(string projectId, string path)
    {
        _lastError = null;
        await EnsureBaselineAsync();
        var target = Map(path);
        try
        {
            await Ctx.Tools.Add(projectId, target);
            await Ctx.Tools.Add(projectId, target);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^I call memory_watch_add without a project on path \"([^\"]*)\"$")]
    public async Task WhenCallWatchAddWithoutProject(string path)
    {
        _lastError = null;
        try
        {
            await Ctx.Tools.Add(null!, Map(path));
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^I call memory_watch_remove for \"([^\"]*)\" on path \"([^\"]*)\"$")]
    public async Task WhenCallWatchRemove(string projectId, string path)
    {
        _lastError = null;
        _removedPath = Map(path);
        try
        {
            await Ctx.Tools.Remove(projectId, _removedPath);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^I call memory_watch_remove for \"([^\"]*)\" on the same path$")]
    public async Task WhenCallWatchRemoveSamePath(string projectId)
    {
        _lastError = null;
        _removedPath.ShouldNotBeNull();
        try
        {
            await Ctx.Tools.Remove(projectId, _removedPath!);
        }
        catch (Exception ex)
        {
            _lastError = ex;
        }
    }

    [When("^memory_watch_status for \"([^\"]*)\" is called$")]
    public Task WhenWatchStatusCalled(string projectId) => StatusAsync(projectId);

    [When("^a new file \"([^\"]*)\" is created$")]
    public void WhenNewFileCreated(string path) => GivenAFile(path);

    [When("^a file receives create, change and rename events within one tick$")]
    public void WhenFileReceivesMixedEvents()
    {
        var dir = Ctx.RepoDir;
        var token = NextToken("x");
        Ctx.WriteFile(Path.Combine(dir, "x.md"), $"{token} content");
        File.Move(Path.Combine(dir, "x.md"), Path.Combine(dir, "y.md"));
        Ctx.WriteFile(Path.Combine(dir, "y.md"), $"{token} content");
        File.Move(Path.Combine(dir, "y.md"), Path.Combine(dir, "x.md"), true);
        Ctx.WriteFile(Path.Combine(dir, "x.md"), $"{token} final");
        _contentByPath[Path.Combine(dir, "x.md")] = token;
        _lastFile = Path.Combine(dir, "x.md");
        _lastFileContent = token;
    }

    [When("^a file outside \"([^\"]*)\" changes$")]
    public void WhenFileOutsideChanges(string path)
    {
        var outside = Path.Combine(Ctx.DataRoot, "outside.md");
        var token = NextToken("outside");
        Ctx.WriteFile(outside, $"{token} content");
        _contentByPath[outside] = token;
        _lastFile = outside;
        _lastFileContent = token;
    }

    [When("^a file is created in \"([^\"]*)\"$")]
    public void WhenFileCreatedIn(string path) => WriteFileAt(path, "file.md");

    [When("^the file changes during the digest$")]
    public void WhenFileChangesDuringDigest()
    {
        var path = Path.Combine(Ctx.RepoDir, "file.md");
        var token = NextToken("v2");
        Ctx.WriteFile(path, $"{token} content");
        _contentByPath[path] = token;
        _lastFile = path;
        _lastFileContent = token;
    }

    [When("^a file is saved repeatedly within one tick$")]
    public void WhenFileSavedRepeatedly()
    {
        var path = Path.Combine(Ctx.RepoDir, "file.md");
        for (var i = 1; i <= 3; i++)
        {
            var token = NextToken("save");
            Ctx.WriteFile(path, $"{token} content");
            _contentByPath[path] = token;
            if (!_contentsByPath.TryGetValue(path, out var list))
            {
                list = [];
                _contentsByPath[path] = list;
            }

            list.Add(token);
            _lastFile = path;
            _lastFileContent = token;
        }
    }

    [When("^the server restarts and re-watches$")]
    public async Task WhenServerRestartsAndRewatches()
    {
        Ctx.Restart();
        await Ctx.ReconcileOnceAsync();
    }

    [When("^\"([^\"]*)\" is deleted$")]
    public void WhenFileDeleted(string file)
    {
        var path = Resolve(file);
        File.Delete(path);
        _lastFile = path;
        _lastFileContent = _contentByPath.TryGetValue(path, out var token) ? token : null;
    }

    [When("^\"([^\"]*)\" is deleted before the digest runs$")]
    public void WhenFileDeletedBeforeDigestRuns(string file)
    {
        var path = Resolve(file);
        File.Delete(path);
        _lastFile = path;
        _lastFileContent = _contentByPath.TryGetValue(path, out var token) ? token : null;
    }

    [When("^a file that was never ingested is deleted$")]
    public void WhenNeverIngestedFileDeleted()
    {
        var path = Path.Combine(Ctx.RepoDir, "tmp.md");
        Ctx.WriteFile(path, "zephyrtmp content");
        File.Delete(path);
    }

    [When("^\"([^\"]*)\" is renamed to \"([^\"]*)\"$")]
    public void WhenFileRenamed(string file, string newFile)
    {
        var oldPath = Resolve(file);
        var newPath = Resolve(newFile);
        _displacedContent = null;
        File.Move(oldPath, newPath);
        _lastFile = newPath;
        _lastFileContent = _contentByPath.TryGetValue(oldPath, out var token) ? token : null;
        if (token is not null)
        {
            _contentByPath[newPath] = token;
        }
    }

    [When("^\"([^\"]*)\" is renamed onto \"([^\"]*)\"$")]
    public void WhenFileRenamedOnto(string file, string newFile)
    {
        var oldPath = Resolve(file);
        var newPath = Resolve(newFile);
        _displacedContent = _contentByPath.TryGetValue(newPath, out var existing) ? existing : null;
        File.Move(oldPath, newPath, true);
        _lastFile = newPath;
        _lastFileContent = _contentByPath.TryGetValue(oldPath, out var token) ? token : null;
        if (token is not null)
        {
            _contentByPath[newPath] = token;
        }
    }

    [When("^a file is renamed during the scan$")]
    public void WhenFileRenamedDuringScan()
    {
        var oldPath = Path.Combine(Ctx.RepoDir, "old.md");
        var newPath = Path.Combine(Ctx.RepoDir, "new.md");
        File.Move(oldPath, newPath);
        _contentByPath[newPath] = _contentByPath[oldPath];
        _lastFile = newPath;
        _lastFileContent = _contentByPath[newPath];
    }

    [When("^\"([^\"]*)\" is touched without a content change$")]
    public void WhenFileTouched(string file) => File.SetLastWriteTimeUtc(Resolve(file), Ctx.TimeProvider.GetUtcNow().UtcDateTime);

    [When("^\"([^\"]*)\" content changes$")]
    public void WhenFileContentChanges(string file)
    {
        var path = Resolve(file);
        var token = NextToken("v2");
        Ctx.WriteFile(path, $"{token} content");
        _contentByPath[path] = token;
        _lastFile = path;
        _lastFileContent = token;
    }

    [When("^a file changes under the watch$")]
    public void WhenFileChangesUnderWatch() => WhenFileCreatedIn("/repo");

    [When("^a change arrives$")]
    public async Task WhenChangeArrives()
    {
        var file = BrokenFile(Ctx.RepoDir);
        MakeReadable(file);
        var token = NextToken("recovered");
        Ctx.WriteFile(file, $"{token} content");
        _contentByPath[file] = token;
        await Task.Delay(150);
    }

    [When("^the failure occurs$")]
    public async Task WhenFailureOccurs()
    {
        if (IsUnreadableSupported)
        {
            await DriveFailuresAsync(BrokenFile(Ctx.RepoDir), 1, true);
        }
    }

    [When("^the next digest succeeds$")]
    public async Task WhenNextDigestSucceeds()
    {
        var file = BrokenFile(Ctx.RepoDir);
        MakeReadable(file);
        var token = NextToken("recovered");
        Ctx.WriteFile(file, $"{token} content");
        _contentByPath[file] = token;
        await Task.Delay(150);
        Ctx.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
    }

    [When("^five consecutive digests fail$")]
    public async Task WhenFiveConsecutiveDigestsFail()
    {
        if (IsUnreadableSupported)
        {
            await DriveFailuresAsync(BrokenFile(Ctx.RepoDir), 5, true);
        }
    }

    [When("^the tick runs$")]
    [When("^a tick runs$")]
    [When("^the next tick runs$")]
    public async Task WhenTickRuns()
    {
        _beforeSearch ??= await SearchAsync(DefaultProject, "zephyr");
        await Ctx.RunTickAsync();
    }

    [When("^the tick processes$")]
    [When("^the tick processes them$")]
    [When("^the tick processes it$")]
    public async Task WhenTickProcesses()
    {
        _beforeSearch ??= await SearchAsync(DefaultProject, "zephyr");
        await Ctx.RunTickAsync();
    }

    [When("^the watcher loop hits an exception$")]
    public async Task WhenWatcherLoopHitsException()
    {
        var file = BrokenFile(Ctx.RepoDir);
        Ctx.WriteFile(file, "zephyrbroken content");
        if (IsUnreadableSupported)
        {
            MakeUnreadable(file);
            await Task.Delay(150);
            await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
        }
    }

    [When("^the mirror removes chunks for a deleted file$")]
    public async Task WhenMirrorRemovesChunksForDeletedFile()
    {
        await SetupWatchAsync(DefaultProject, "/repo");
        WriteFileAt("/repo", "file.md");
        var path = Path.Combine(Ctx.RepoDir, "file.md");
        (await EnsureSearchableAsync(DefaultProject, _contentByPath[path], path)).ShouldBeTrue();
        File.Delete(path);
        (await EnsureNotSearchableAsync(DefaultProject, _contentByPath[path])).ShouldBeTrue();
    }

    // ── Thens ──
    [Then("^the tool errors with (watching-disabled|path-outside-scope|path-not-found)$")]
    public void ThenToolErrors(string code)
    {
        _lastError.ShouldNotBeNull("expected a tool error");
        // A direct call sees the raw domain exception; the CallToolFilter is what maps it to this
        // wire prefix (see ToolRefusalsTests) — read that same mapping table here.
        ToolRefusals.PrefixFor(_lastError!).ShouldBe(code);
    }

    // "the tool errors with access-denied" is bound in NativeMemorySteps (reads
    // scenarioContext["LastError"], set above); also pinned by WatchToolsAccessModeTests.

    [Then("^the tool errors with missing-project$")]
    public void ThenToolErrorsMissingProject()
    {
        _lastError.ShouldNotBeNull().ShouldBeOfType<McpException>();
        _lastError!.Message.ShouldContain("project_id");
    }

    [Then("^the tool returns success$")]
    public void ThenToolReturnsSuccess() => _lastError.ShouldBeNull();

    [Then("^a watch exists for \"([^\"]*)\" at \"([^\"]*)\"$")]
    public async Task ThenWatchExists(string projectId, string path)
    {
        var status = await StatusAsync(projectId);
        status.Watches.Select(w => Normalized(w.Path)).ShouldContain(Normalized(Map(path)));
    }

    [Then("^both watches exist$")]
    public async Task ThenBothWatchesExist()
    {
        await ThenWatchExists("proj-a", "/repo");
        await ThenWatchExists("proj-b", "/repo");
    }

    [Then("^exactly one watch exists for \"([^\"]*)\" at \"([^\"]*)\"$")]
    public async Task ThenExactlyOneWatchExists(string projectId, string path)
    {
        var status = await StatusAsync(projectId);
        status.Watches.Count(w => Normalized(w.Path) == Normalized(Map(path))).ShouldBe(1);
    }

    [Then("^a second watch exists for \"([^\"]*)\" at \"([^\"]*)\"$")]
    public async Task ThenSecondWatchExists(string projectId, string path)
    {
        await ThenWatchExists("proj-a", "/repo");
        await ThenWatchExists(projectId, path);
    }

    [Then("^the watch is created$")]
    public async Task ThenWatchCreated()
    {
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count.ShouldBeGreaterThan(0);
    }

    [Then("^no watch exists for the path$")]
    public async Task ThenNoWatchExistsForPath()
    {
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count(w => Normalized(w.Path) == Normalized(_removedPath!)).ShouldBe(0);
    }

    [Then("^the watch stays removed$")]
    public async Task ThenWatchStaysRemoved()
    {
        var registrations = await Ctx.WatchStore.ListWatchesAsync();
        registrations.Any(r => r.ProjectId == DefaultProject && Normalized(r.Path) == Normalized(_removedPath!))
            .ShouldBeFalse();
    }

    [Then("^the watch remains registered$")]
    public async Task ThenWatchRemainsRegistered()
    {
        var registrations = await Ctx.WatchStore.ListWatchesAsync();
        registrations.Any(r => r.ProjectId == DefaultProject && Normalized(r.Path) == Normalized(Ctx.RepoDir))
            .ShouldBeTrue();
    }

    [Then("^memory_watch_status for \"([^\"]*)\" shows the watch scanning$")]
    public async Task ThenStatusShowsScanning(string projectId)
    {
        var status = await StatusAsync(projectId);
        status.Watches.ShouldNotBeEmpty();
        status.Watches[0].State.ShouldBe(WatchState.Scanning);
    }

    [Then("^the watch state is scanning$")]
    public async Task ThenWatchStateScanning()
    {
        var status = await StatusAsync(DefaultProject);
        status.Watches.ShouldNotBeEmpty();
        status.Watches[0].State.ShouldBe(WatchState.Scanning);
    }

    [Then("^memory_watch_status for \"([^\"]*)\" shows the scan error$")]
    [Then("^memory_watch_status for \"([^\"]*)\" shows the error for that watch$")]
    [Then("^memory_watch_status for \"([^\"]*)\" shows the error$")]
    public async Task ThenStatusShowsError(string projectId)
    {
        await Ctx.ReconcileOnceAsync();
        var ok = await Ctx.StepUntilAsync(async () =>
        {
            var status = await StatusAsync(projectId);
            return status.Watches.Count > 0 && status.Watches[0].LastError is not null;
        });
        ok.ShouldBeTrue("the watch error did not surface in status within the poll bound");
    }

    [Then("^it returns the watch states including the error$")]
    public async Task ThenStatusReturnsStatesIncludingError()
    {
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count.ShouldBeGreaterThan(0);
        status.Watches.Any(w => w.LastError is not null).ShouldBeTrue();
    }

    [Then("^memory_watch_status for \"([^\"]*)\" no longer shows the error$")]
    public async Task ThenStatusNoLongerShowsError(string projectId)
    {
        var status = await StatusAsync(projectId);
        status.Watches.ShouldNotBeEmpty();
        status.Watches[0].LastError.ShouldBeNull();
    }

    [Then("^memory_watch_status for \"([^\"]*)\" reports the watch as stopped$")]
    public async Task ThenStatusReportsStopped(string projectId)
    {
        var status = await StatusAsync(projectId);
        status.Watches.ShouldNotBeEmpty();
        status.Watches[0].State.ShouldBe(WatchState.Stopped);
    }

    [Then("^memory_watch_status for \"([^\"]*)\" reports the watch as retrying$")]
    public async Task ThenStatusReportsRetrying(string projectId)
    {
        // The retry that follows the backoff succeeded (Then "the digest is attempted again");
        // a fresh failure after the reset must report retrying — the 4 failures never stopped it.
        if (IsUnreadableSupported)
        {
            var file = BrokenFile(Ctx.RepoDir);
            QueueUnreadableChange(file);
            await Task.Delay(150);
            Ctx.TimeProvider.Advance(TimeSpan.FromSeconds(1));
            await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
        }

        var status = await StatusAsync(projectId);
        status.Watches.ShouldNotBeEmpty();
        status.Watches[0].State.ShouldBe(WatchState.Retrying);
    }

    [Then("^memory_watch_status for \"([^\"]*)\" reports the watch healthy$")]
    public async Task ThenStatusReportsHealthy(string projectId)
    {
        var status = await StatusAsync(projectId);
        status.Watches.ShouldNotBeEmpty();
        status.Watches[0].State.ShouldBe(WatchState.Healthy);
    }

    [Then("^memory_watch_status for \"([^\"]*)\" stays healthy$")]
    public async Task ThenStatusStaysHealthy(string projectId)
    {
        var ok = await Ctx.StepUntilAsync(async () =>
        {
            var status = await StatusAsync(projectId);
            return status.Watches.Count > 0 && status.Watches[0].State == WatchState.Healthy;
        });
        ok.ShouldBeTrue("the watch did not reach healthy within the poll bound");
    }

    [Then("^memory_watch_status for \"([^\"]*)\" shows no watch for it$")]
    public async Task ThenStatusShowsNoWatch(string projectId)
    {
        var status = await StatusAsync(projectId);
        status.Watches.Count.ShouldBe(0);
    }

    [Then("^memory_watch_status for \"([^\"]*)\" still answers$")]
    public async Task ThenStatusStillAnswers(string projectId)
    {
        var status = await StatusAsync(projectId);
        status.ShouldNotBeNull();
    }

    [Then("^the watch states are returned$")]
    public async Task ThenWatchStatesReturned()
    {
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count.ShouldBeGreaterThan(0);
    }

    [Then("^an empty list is returned without error$")]
    public async Task ThenEmptyListReturned()
    {
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count.ShouldBe(0);
        _lastError.ShouldBeNull();
    }

    [Then("^the call returns with the watch registered$")]
    public async Task ThenCallReturnsWithWatchRegistered()
    {
        _lastError.ShouldBeNull();
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count.ShouldBeGreaterThan(0);
    }

    [Then("^the call returns without waiting for the scan to finish$")]
    public async Task ThenCallReturnsWithoutWaiting()
    {
        _lastError.ShouldBeNull();
        var status = await StatusAsync(DefaultProject);
        status.Watches.Count.ShouldBeGreaterThan(0);
        status.Watches[0].State.ShouldBe(WatchState.Scanning);
        (await SearchAsync(DefaultProject, "zephyrword0")).Count.ShouldBe(0,
            "nothing is ingested until the background scan runs (rule 4)");
    }

    [Then("^the call still returns$")]
    public void ThenCallStillReturns() => _lastError.ShouldBeNull();

    [Then("^the MCP server keeps serving$")]
    public async Task ThenMcpServerKeepsServing()
    {
        var results = await SearchAsync(DefaultProject, "zephyr");
        results.ShouldNotBeNull();
    }

    [Then("^memory_search for \"([^\"]*)\" returns the file's content$")]
    [Then("^memory_search for \"([^\"]*)\" returns its content$")]
    public async Task ThenSearchReturnsFilesContent(string projectId)
    {
        _lastFile.ShouldNotBeNull();
        var ok = await EnsureSearchableAsync(projectId, _lastFileContent!, _lastFile);
        ok.ShouldBeTrue("the file's content did not become searchable within the poll bound");
    }

    [Then("^it is searchable for \"([^\"]*)\" within one second$")]
    public async Task ThenSearchableWithinOneSecond(string projectId)
    {
        var ok = await EnsureSearchableAsync(projectId, _lastFileContent!, _lastFile, 1);
        ok.ShouldBeTrue("the content did not become searchable within one (fake) second");
    }

    [Then("^the new content is searchable within one second$")]
    public async Task ThenNewContentSearchableWithinOneSecond()
    {
        var ok = await EnsureSearchableAsync(DefaultProject, _lastFileContent!, _lastFile, 1);
        ok.ShouldBeTrue("the new content did not become searchable within one (fake) second");
    }

    [Then("^the new content is searchable after that tick$")]
    public async Task ThenNewContentSearchableAfterThatTick()
    {
        var ok = await EnsureSearchableAsync(DefaultProject, _lastFileContent!, _lastFile);
        ok.ShouldBeTrue("the new content was not searchable after the tick");
    }

    [Then("^the newer content is searchable after the following tick$")]
    public async Task ThenNewerContentSearchableAfterFollowingTick()
    {
        var ok = await EnsureSearchableAsync(DefaultProject, _lastFileContent!, _lastFile);
        ok.ShouldBeTrue("the newer content was not searchable after the following tick");
    }

    [Then("^the final content is searchable for \"([^\"]*)\"$")]
    public async Task ThenFinalContentSearchableFor(string projectId)
    {
        var ok = await EnsureSearchableAsync(projectId, _lastFileContent!, _lastFile);
        ok.ShouldBeTrue("the final content was not searchable within the poll bound");
    }

    [Then("^the final content is searchable$")]
    public async Task ThenFinalContentSearchable()
    {
        var ok = await EnsureSearchableAsync(DefaultProject, _lastFileContent!, _lastFile);
        ok.ShouldBeTrue("the final content was not searchable within the poll bound");
    }

    [Then("^the new content is searchable$")]
    public async Task ThenNewContentSearchable()
    {
        var ok = await EnsureSearchableAsync(DefaultProject, _lastFileContent!, _lastFile);
        ok.ShouldBeTrue("the new content was not searchable within the poll bound");
    }

    [Then("^no intermediate save's content is ever searchable$")]
    public async Task ThenNoIntermediateSaveEverSearchable()
    {
        _lastFile.ShouldNotBeNull();
        var all = _contentsByPath[_lastFile!];
        all.Count.ShouldBeGreaterThan(1);
        foreach (var token in all.Take(all.Count - 1))
        {
            (await SearchAsync(DefaultProject, token)).Count.ShouldBe(0,
                $"intermediate content '{token}' must never be searchable (replace-by-path)");
        }
    }

    [Then("^the memory entry is unchanged$")]
    public async Task ThenMemoryEntryUnchanged()
    {
        _lastFile.ShouldNotBeNull();
        var path = _lastFile!;
        (await CountEntriesAsync(path, null)).ShouldBe(1, "the file must be ingested exactly once before the touch");
        // The touch event fires (LastWrite notify filter); let a tick process it, then the entry
        // count must still be exactly the ingested one (hash-skip: no re-ingest on metadata-only change).
        await Task.Delay(150);
        await Ctx.RunTickAsync();
        await Task.Delay(150);
        await Ctx.RunTickAsync();
        (await CountEntriesAsync(path, null)).ShouldBe(1, "hash-skip must not re-ingest a metadata-only touch");
        _lastFileContent.ShouldNotBeNull();
        (await SearchAsync(DefaultProject, _lastFileContent!)).Count.ShouldBe(1);
    }

    [Then("^memory_search for \"([^\"]*)\" returns nothing for it$")]
    public async Task ThenSearchReturnsNothing(string projectId)
    {
        _lastFileContent.ShouldNotBeNull();
        (await SearchAsync(projectId, _lastFileContent!)).Count.ShouldBe(0);
    }

    [Then("^memory_search for \"([^\"]*)\" no longer returns its content$")]
    public async Task ThenSearchNoLongerReturnsContent(string projectId)
    {
        _lastFileContent.ShouldNotBeNull();
        var ok = await EnsureNotSearchableAsync(projectId, _lastFileContent!);
        ok.ShouldBeTrue("the deleted file's content is still searchable");
    }

    [Then("^memory_search for \"([^\"]*)\" returns the content under \"([^\"]*)\"$")]
    public async Task ThenSearchReturnsContentUnder(string projectId, string path)
    {
        var real = Resolve(path);
        var ok = await EnsureSearchableAsync(projectId, _lastFileContent!, real);
        ok.ShouldBeTrue("the content did not land under the new path");
        var expected = Normalized(real);
        var results = await SearchAsync(projectId, _lastFileContent!);
        results.All(r => Normalized(r.SourceFile ?? string.Empty) == expected).ShouldBeTrue();
    }

    [Then("^nothing is returned under \"([^\"]*)\"$")]
    public async Task ThenNothingReturnedUnder(string path)
    {
        _lastFileContent.ShouldNotBeNull();
        var results = await SearchAsync(DefaultProject, _lastFileContent!);
        results.Any(r => Normalized(r.SourceFile ?? string.Empty) == Normalized(Resolve(path))).ShouldBeFalse();
    }

    [Then("^the content lands under the final path$")]
    public async Task ThenContentLandsUnderFinalPath()
    {
        var newPath = Path.Combine(Ctx.RepoDir, "new.md");
        var ok = await EnsureSearchableAsync(DefaultProject, _lastFileContent!, newPath);
        ok.ShouldBeTrue("the renamed file's content did not land under the final path");
        (await SearchAsync(DefaultProject, _lastFileContent!))
            .Any(r => Normalized(r.SourceFile ?? string.Empty) == Normalized(Path.Combine(Ctx.RepoDir, "old.md")))
            .ShouldBeFalse();
    }

    [Then("^\"([^\"]*)\" is re-digested and its new content is searchable$")]
    public async Task ThenFileRedigestedAndSearchable(string file)
    {
        var path = Path.Combine(Ctx.RepoDir, file);
        var ok = await EnsureSearchableAsync(DefaultProject, _contentByPath[path], path);
        ok.ShouldBeTrue("the changed file was not re-digested on restart");
    }

    [Then("^the file is not present in memory after the next tick$")]
    public async Task ThenFileNotPresentAfterNextTick()
    {
        var path = Path.Combine(Ctx.RepoDir, "file.md");
        var ok = await Ctx.StepUntilAsync(async () => await CountEntriesAsync(path, null) == 0);
        ok.ShouldBeTrue("the deleted file's chunks are still present");
        _lastFileContent.ShouldNotBeNull();
        (await SearchAsync(DefaultProject, _lastFileContent!)).Count.ShouldBe(0);
    }

    [Then("^exactly one memory entry holds that path$")]
    public async Task ThenExactlyOneContentSetHoldsThatPath()
    {
        // "Exactly one memory entry" means one CONTENT SET (old content absent, new content
        // present, no orphan chunks), not a row-count-of-1 (docs/plans/file-watcher-implementation.md).
        var target = Path.Combine(Ctx.RepoDir, "b.md");
        var source = Path.Combine(Ctx.RepoDir, "a.md");
        var incoming = _contentByPath[source];
        var displaced = _displacedContent ?? _contentByPath[target];

        var ok = await Ctx.StepUntilAsync(async () =>
        {
            var incomingResults = await SearchAsync(DefaultProject, incoming);
            return incomingResults.Count > 0 &&
                   incomingResults.All(r => Normalized(r.SourceFile ?? string.Empty) == Normalized(target));
        });
        ok.ShouldBeTrue("the incoming content did not land under the target path");

        (await SearchAsync(DefaultProject, displaced)).Count.ShouldBe(0,
            "D2 overwrite: the target's previous content must be replaced");
        (await CountEntriesAsync(source, null)).ShouldBe(0, "the source path must have no orphan chunks");
        (await CountEntriesAsync(target, displaced)).ShouldBe(0);
        (await CountEntriesAsync(target, incoming)).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Then("^a change to \"([^\"]*)\" is searchable in both projects' banks$")]
    public async Task ThenChangeSearchableInBothBanks(string path)
    {
        var real = Map(path);
        var token = NextToken("shared");
        Ctx.WriteFile(real, $"{token} content");
        _contentByPath[real] = token;
        (await EnsureSearchableAsync("proj-a", token, real)).ShouldBeTrue();
        (await EnsureSearchableAsync("proj-b", token, real)).ShouldBeTrue();
    }

    [Then("^editing the file makes the new content searchable$")]
    public async Task ThenEditingFileMakesNewContentSearchable()
    {
        var real = Map("/repo/readme.md");
        var token = NextToken("edited");
        Ctx.WriteFile(real, $"{token} content");
        _contentByPath[real] = token;
        // The watched path is a bare FILE, and FileSystemWatcher cannot watch a single file (only
        // a directory), so the edit event is delivered via the pipeline's Enqueue seam instead.
        Ctx.Pipeline.Enqueue(new WatchEvent(DefaultProject, real, WatchEventKind.Changed));
        (await EnsureSearchableAsync(DefaultProject, token, real)).ShouldBeTrue(
            "editing the watched file did not become searchable");
    }

    [Then("^editing \"([^\"]*)\" makes the new content searchable$")]
    public async Task ThenEditingPathMakesNewContentSearchable(string path)
    {
        var real = Resolve(path);
        var token = NextToken("edited");
        Ctx.WriteFile(real, $"{token} content");
        _contentByPath[real] = token;
        (await EnsureSearchableAsync(DefaultProject, token, real)).ShouldBeTrue(
            "editing the watched file did not become searchable");
    }

    [Then("^all ten files are searchable in their projects$")]
    public async Task ThenAllTenSearchable()
    {
        foreach (var (project, path, token) in _searchables)
        {
            (await EnsureSearchableAsync(project, token, path)).ShouldBeTrue($"{path} not searchable");
        }
    }

    [Then("^all four files are searchable$")]
    public async Task ThenAllFourSearchable()
    {
        foreach (var (project, path, token) in _searchables)
        {
            (await EnsureSearchableAsync(project, token, path)).ShouldBeTrue($"{path} not searchable");
        }
    }

    [Then("^the small watch's change is searchable promptly$")]
    public async Task ThenSmallWatchChangeSearchablePromptly()
    {
        var (project, path, token) = _searchables.Single();
        (await EnsureSearchableAsync(project, token, path, 1)).ShouldBeTrue(
            "the small watch's change was not searchable promptly");
    }

    [Then("^the large watch's changes are searchable too$")]
    public async Task ThenLargeWatchChangesSearchableToo()
    {
        for (var i = 0; i < 20; i++)
        {
            var path = Path.Combine(Map("/repo-a"), $"big{i}.md");
            var token = _contentByPath[path];
            (await EnsureSearchableAsync("proj-a", token, path)).ShouldBeTrue($"{path} not searchable");
        }
    }

    [Then("^memory_search for \"([^\"]*)\" returns the same results as before$")]
    public async Task ThenSearchSameResultsAsBefore(string projectId)
    {
        _beforeSearch.ShouldNotBeNull();
        var after = await SearchAsync(projectId, "zephyr");
        after.Count.ShouldBe(_beforeSearch!.Count);
    }

    [Then("^memory_search for \"([^\"]*)\" still answers$")]
    public async Task ThenSearchStillAnswers(string projectId)
    {
        var results = await SearchAsync(projectId, "zephyr");
        results.ShouldNotBeNull();
    }

    [Then("^memory_watch_add for \"([^\"]*)\" on another path still works$")]
    public async Task ThenWatchAddOnAnotherPathStillWorks(string projectId)
    {
        _lastError = null;
        await Ctx.Tools.Add(projectId, Ctx.RepoDir);
        var status = await StatusAsync(projectId);
        status.Watches.Count(w => Normalized(w.Path) == Normalized(Ctx.RepoDir)).ShouldBe(1);
    }

    [Then("^the digest is attempted again$")]
    public async Task ThenDigestAttemptedAgain()
    {
        var file = BrokenFile(Ctx.RepoDir);
        var token = _contentByPath[file];
        Ctx.TimeProvider.Advance(TimeSpan.FromSeconds(8)); // backoff of failure #4
        await Ctx.Pipeline.TickOnceAsync(CancellationToken.None);
        var ok = await Ctx.StepUntilAsync(async () => (await SearchAsync(DefaultProject, token)).Count > 0);
        ok.ShouldBeTrue("the 5th digest was not attempted after the backoff");
    }

    [Then("^no digest happens for that path$")]
    public async Task ThenNoDigestHappensForPath()
    {
        var path = Path.Combine(Ctx.RepoDir, "file.md");
        (await CountEntriesAsync(path, null)).ShouldBe(0);
    }

    [Then("^each project digests into its own bank$")]
    public async Task ThenEachProjectDigestsIntoOwnBank()
    {
        var real = Path.Combine(Ctx.RepoDir, "file.md");
        var token = NextToken("ownbank");
        Ctx.WriteFile(real, $"{token} content");
        _contentByPath[real] = token;
        (await EnsureSearchableAsync("proj-a", token, real)).ShouldBeTrue();
        (await EnsureSearchableAsync("proj-b", token, real)).ShouldBeTrue();
    }

    [Then("^the removal succeeds without tier denial$")]
    public void ThenRemovalSucceedsWithoutTierDenial() => _lastError.ShouldBeNull();

    [Then("^memory_watch_remove for \"([^\"]*)\" on path \"([^\"]*)\" removes it$")]
    public async Task ThenWatchRemoveRemovesIt(string projectId, string path)
    {
        _lastError = null;
        await Ctx.Tools.Remove(projectId, Map(path));
        var status = await StatusAsync(projectId);
        status.Watches.Count(w => Normalized(w.Path) == Normalized(Map(path))).ShouldBe(0);
    }

    [Then("^later failures do not stop it prematurely$")]
    public async Task ThenLaterFailuresDoNotStopPrematurely()
    {
        if (IsUnreadableSupported)
        {
            await DriveFailuresAsync(BrokenFile(Ctx.RepoDir), 1, false);
            var status = await StatusAsync(DefaultProject);
            status.Watches[0].State.ShouldBe(WatchState.Retrying,
                "the counter reset on success — one new failure must not stop the watch");
        }
    }
}
