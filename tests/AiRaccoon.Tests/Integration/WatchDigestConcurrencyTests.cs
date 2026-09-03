using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Projects;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Two server processes digesting one path against one bank: the file must stay searchable
///     throughout the digest, and its content must be chunked once, not once per process.
///     The interleaving is forced at the chunker seam, not timed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(WatchIntegrationCollection.Name)]
public sealed class WatchDigestConcurrencyTests
{
    private const string Project = "acme";

    /// <summary>Present in both revisions: a search for it may never come back empty.</summary>
    private const string Sentinel = "zephyrsentinel";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [RetryFact]
    public async Task TwoProcessesDigestingOnePath_KeepItSearchable_AndChunkItOnce()
    {
        var token = TestContext.Current.CancellationToken;
        using var bank = new Bank("digest-race");
        var one = bank.TestProcess();
        var two = bank.TestProcess();
        var reader = bank.TestProcess();
        await bank.SeedAsync(one, $"{Sentinel} v1body", token);

        bank.Write($"{Sentinel} v2body");
        one.Gate.Arm();
        two.Gate.Arm();
        var first = Task.Run(() => bank.DigestAsync(one, token), token);
        var second = Task.Run(() => bank.DigestAsync(two, token), token);

        // Observe from a third connection while a digest is parked inside its write window.
        await Task.WhenAny(one.Gate.Entered, two.Gate.Entered).WaitAsync(Patience, token);
        var midDigest = await bank.RetrievableChunksAsync(reader, Sentinel, token);
        one.Gate.Release();
        two.Gate.Release();
        await Task.WhenAll(first, second).WaitAsync(Patience, token);

        midDigest.ShouldBeGreaterThan(0, "the watched file vanished from search while a digest was in flight");
        (one.Gate.Calls + two.Gate.Calls).ShouldBe(1,
            "the same file was chunked and embedded once per process instead of once");
        (await bank.SearchAsync(reader, "v2body", token)).ShouldNotBeEmpty();
        (await bank.CountEntriesAsync(reader, "v1body", token)).ShouldBe(0, "mirror semantics: v1 must be replaced");
        (await one.WatchStore.GetFileHashAsync(Project, bank.FilePath, token))
            .ShouldBe(WatchDigestExecutor.ComputeHash(bank.FilePath, $"{Sentinel} v2body"));
    }

    [RetryFact]
    public async Task DigestThatFailsMidIngest_LeavesThePreviousContentSearchable()
    {
        var token = TestContext.Current.CancellationToken;
        using var bank = new Bank("digest-crash");
        var one = bank.TestProcess();
        var reader = bank.TestProcess();
        await bank.SeedAsync(one, $"{Sentinel} v1body", token);

        bank.Write($"{Sentinel} v2body");
        one.Gate.Arm();
        one.Gate.FailWith(new IOException("ingest died mid-digest"));

        await Should.ThrowAsync<IOException>(() => bank.DigestAsync(one, token));

        (await bank.SearchAsync(reader, Sentinel, token))
            .ShouldNotBeEmpty("a digest that failed after the delete left the file with no chunks at all");
    }

    /// <summary>
    ///     A digest parked mid-write while the rename runs: the cross-store window (watches moved,
    ///     scope still loser-keyed) resolves blank ids as Ambiguous — fail-closed, never guessed —
    ///     the scan lease rides the UPDATE to the winner row, the stale completion lands raw under
    ///     its pre-rename id without corrupting anything, and a redriven repair folds every
    ///     moveable remainder while the NULL-context file mirrors stay byte-identical (review S2).
    ///     Ledger — delete-insert-instead-of-update : --filter RenameDuringInflightScan_FailsClosed :
    ///     parked v2 digest across a stepped rename (rowid/lease asserts); skip-watches-step : same :
    ///     mid-rename Ambiguous assert; skip-redrive : same : post-redrive loser-zero asserts.
    /// </summary>
    [RetryFact]
    public async Task RenameDuringInflightScan_FailsClosed_ThenConvergesOnRedrive()
    {
        const string loser = "job-search-ai-assistant";
        const string winner = "jsaa";
        var token = TestContext.Current.CancellationToken;
        using var bank = new Bank("rename-race");
        var one = bank.TestProcess();
        var watchDir = Path.GetDirectoryName(bank.FilePath)!;

        await one.Memory.SetSettingAsync(IngestScopeKeys.ScopeGlobal,
            IngestScopeKeys.Serialize([watchDir]), token);
        // Raw loser key on purpose: the factory folds at construction now, so only a pre-repair
        // bank still holds this spelling — half of the transient window below.
        await one.Memory.SetSettingAsync("ingest.scope." + loser,
            IngestScopeKeys.Serialize([watchDir]), token);
        await one.WatchStore.AddWatchAsync(loser, watchDir, 0, 0, token);
        bank.Write($"{Sentinel} v1body");
        await bank.DigestAsync(one, token, loser);
        (await bank.CountEntriesAsync(one, "v1body", token, loser))
            .ShouldBeGreaterThan(0, "arrange: v1 searchable under the loser");

        long rowid;
        await using (var connection = await one.Factory.OpenBankAsync(token))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE watches SET scan_owner = 'scanner-1', scan_lease_expires_at = 1700003600 WHERE project_id = @l",
                new { l = loser }, cancellationToken: token));
            rowid = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT rowid FROM watches WHERE project_id = @l", new { l = loser },
                cancellationToken: token));
        }

        // Park a v2 digest inside its write window, then run the repair's watches step alone.
        bank.Write($"{Sentinel} v2body");
        one.Gate.Arm();
        var stale = Task.Run(() => bank.DigestAsync(one, token, loser), token);
        await one.Gate.Entered.WaitAsync(Patience, token);
        await using (var connection = await one.Factory.OpenBankAsync(token))
        {
            // The repair's watches UPDATE in isolation (FoldWatchesAsync's statement shape).
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE watches SET project_id = @w WHERE project_id = @l " +
                "AND NOT EXISTS (SELECT 1 FROM watches w WHERE w.project_id = @w AND w.path = watches.path)",
                new { w = winner, l = loser }, cancellationToken: token));
        }

        // The transient blank-id Ambiguous window, NAMED: scopes still name the loser while
        // watches already moved — fail-closed by design, never resolved across fragments.
        var resolution = await new CwdProjectIdResolver(
                new SqliteSettingsStore(one.Factory), new WatchStore(one.Factory),
                new PinnedProbe(watchDir))
            .ResolveAsync(token);
        var ambiguous = resolution.ShouldBeOfType<ProjectIdResolution.Ambiguous>();
        ambiguous.SortedIds.ShouldBe([loser, winner]);

        // The stale digest completes under its pre-rename id: no crash, no corruption — it lands raw.
        one.Gate.Release();
        await stale.WaitAsync(Patience, token);
        (await bank.CountEntriesAsync(one, "v2body", token, loser))
            .ShouldBeGreaterThan(0, "stale completion lands under its pre-rename id");

        // The full repair folds every remaining surface: the lease rides the UPDATE (same rowid),
        // moveable loser surfaces go to zero, NULL-context mirrors stay byte-identical.
        await using (var connection = await one.Factory.OpenBankAsync(token))
        {
            var plan = ProjectIdsFoldPlan.FromCensus(
                await ProjectIdCensus.CollectAsync(connection, token), ProjectIdAliasMap.Default);
            await new ProjectIdsRepair(TimeProvider.System).ApplyAsync(connection, plan, token);
        }

        await using (var after = await one.Factory.OpenBankAsync(token))
        {
            var kept = await after.QueryFirstOrDefaultAsync<(string? Owner, long Lease, long RowId)>(
                new CommandDefinition(
                    "SELECT scan_owner AS Owner, scan_lease_expires_at AS Lease, rowid AS RowId " +
                    "FROM watches WHERE project_id = @w",
                    new { w = winner }, cancellationToken: token));
            kept.Owner.ShouldBe("scanner-1");
            kept.Lease.ShouldBe(1700003600);
            kept.RowId.ShouldBe(rowid, "the rename is an UPDATE preserving the row");
            foreach (var table in new[] { "watches", "watch_files", "watch_digest_claims" })
            {
                (await after.ExecuteScalarAsync<long>(new CommandDefinition(
                        $"SELECT count(*) FROM {table} WHERE project_id = @l",
                        new { l = loser }, cancellationToken: token)))
                    .ShouldBe(0, $"moveable loser surface survived in {table}");
            }
        }

        // Fresh work under the winner is clean: v3 lands under jsaa, the stale loser rows unchanged.
        var nullBefore = await NullContextCountAsync(one, loser, token);
        bank.Write($"{Sentinel} v3body");
        await bank.DigestAsync(one, token, winner);
        (await bank.CountEntriesAsync(one, "v3body", token, winner))
            .ShouldBeGreaterThan(0, "fresh winner digest lands under the winner");
        (await NullContextCountAsync(one, loser, token)).ShouldBe(nullBefore,
            "fresh work resurrects nothing under the loser");
        one.Gate.Calls.ShouldBe(2, "both parked digests ran through the chunker");

        // A redriven repair folds the stale file row back and changes nothing else.
        await using (var redrive = await one.Factory.OpenBankAsync(token))
        {
            var plan = ProjectIdsFoldPlan.FromCensus(
                await ProjectIdCensus.CollectAsync(connection: redrive, cancellationToken: token),
                ProjectIdAliasMap.Default);
            await new ProjectIdsRepair(TimeProvider.System).ApplyAsync(redrive, plan, token);
            (await redrive.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM watch_files WHERE project_id = @l",
                    new { l = loser }, cancellationToken: token)))
                .ShouldBe(0, "the stale file row folds back on redrive");
            (await NullContextCountAsync(one, loser, token)).ShouldBe(nullBefore,
                "NULL-context mirrors stay byte-identical across the redrive (review S2)");
        }
    }

    private static async Task<long> NullContextCountAsync(Process process, string projectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await process.Factory.OpenBankAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = @p AND scope = 'project' AND context_label IS NULL",
            new { p = projectId }, cancellationToken: cancellationToken));
    }

    private sealed class PinnedProbe(string cwd) : AiRaccoon.Projects.ICwdProbe
    {
        public string CurrentDirectory => cwd;
    }

    /// <summary>One bank + watched dir; every <see cref="Process"/> is an independent connection stack over it.</summary>
    private sealed class Bank : IDisposable
    {
        private readonly List<Process> _processes = [];

        public Bank(string name)
        {
            DataRoot = Path.Combine(Path.GetTempPath(), "ai-raccoon-watch-digest-race",
                $"{name}-{Guid.NewGuid():N}");
            WatchDir = Path.Combine(DataRoot, "repo");
            Directory.CreateDirectory(WatchDir);
            FilePath = Path.Combine(WatchDir, "a.md");
        }

        private string DataRoot { get; }

        private string WatchDir { get; }

        public string FilePath { get; }

        public void Dispose()
        {
            TestData.DeleteTempRoot(DataRoot);
        }

        public Process TestProcess()
        {
            var process = new Process(DataRoot);
            _processes.Add(process);
            return process;
        }

        public void Write(string content) => File.WriteAllText(FilePath, content);

        /// <summary>Scope, watch registration and the first revision, ingested and searchable.</summary>
        public async Task SeedAsync(Process process, string content, CancellationToken cancellationToken)
        {
            await process.Memory.SetSettingAsync(IngestScopeKeys.ScopeProject(Project),
                IngestScopeKeys.Serialize([WatchDir]), cancellationToken);
            await process.WatchStore.AddWatchAsync(Project, WatchDir, 0, 0, cancellationToken);
            Write(content);
            await DigestAsync(process, cancellationToken);
            (await SearchAsync(process, Sentinel, cancellationToken))
                .ShouldNotBeEmpty("arrange: the seed revision never became searchable");

            // Every process opens the bank once at startup; the schema DDL that first open runs
            // wants the write lock, so leaving it until mid-digest would measure the wrong thing.
            foreach (var other in _processes)
            {
                await using var connection = await other.Factory.OpenBankAsync(cancellationToken);
            }
        }

        public Task DigestAsync(Process process, CancellationToken cancellationToken,
            string projectId = Project) =>
            process.Executor.DigestAsync(projectId, WatchDir, FilePath, WatchEventKind.Changed, null,
                cancellationToken);

        public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(Process process, string query,
            CancellationToken cancellationToken) =>
            (await process.Memory.SearchAsync(new SearchQuery(Project, query), cancellationToken)).Results;

        /// <summary>
        ///     What a search would retrieve for the file, read-only. The full SearchAsync also bumps
        ///     access counters, and that write would queue behind the digest's transaction rather
        ///     than report what is in the bank at this instant.
        /// </summary>
        public Task<int> RetrievableChunksAsync(Process process, string valueContains,
            CancellationToken cancellationToken) =>
            CountEntriesAsync(process, valueContains, cancellationToken);

        public async Task<int> CountEntriesAsync(Process process, string valueContains,
            CancellationToken cancellationToken, string projectId = Project)
        {
            await using var connection = await process.Factory.OpenBankAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM entries WHERE project_id = @p AND source_file = @path AND value LIKE @value",
                new { p = projectId, path = FilePath, value = $"%{valueContains}%" },
                cancellationToken: cancellationToken));
        }
    }

    /// <summary>One "server process": its own factory, store, watch store and digest executor.</summary>
    private sealed class Process
    {
        public Process(string dataRoot)
        {
            var options = new InfrastructureOptions
            {
                DataRoot = dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
            };
            Factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            var time = new FakeTimeProvider(FixedNow);
            Memory = TestData.CreateMemoryStore(Factory,
                NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(Factory), Gate, time, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
            WatchStore = new WatchStore(Factory);
            Executor = new WatchDigestExecutor(Memory, WatchStore, time, new IgnoreRulesProvider(),
                new Lazy<IWatchScanInitiator>(() => new NoOpWatchScanInitiator()), TestData.NewEmbedDrainPump());
        }

        public SqliteConnectionFactory Factory { get; }

        public GateChunker Gate { get; } = new();

        public SqliteMemoryStore Memory { get; }

        public WatchStore WatchStore { get; }

        public WatchDigestExecutor Executor { get; }
    }

    /// <summary>
    ///     Chunker that parks (or fails) the digest between its delete and its inserts once armed —
    ///     the seam that makes the interleaving deterministic instead of timing-dependent.
    /// </summary>
    private sealed class GateChunker : IMarkdownChunker
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IChunker _inner = TestData.RealMarkdownChunker();

        private readonly ManualResetEventSlim _release = new(false);

        private bool _armed;

        private int _calls;

        private Exception? _failure;

        /// <summary>Chunk calls made while armed — one per full re-chunk of the file.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task Entered => _entered.Task;

        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null)
        {
            if (!_armed)
            {
                return _inner.Chunk(text, maxTokens, overlayTokens);
            }

            Interlocked.Increment(ref _calls);
            _entered.TrySetResult();
            if (_failure is not null)
            {
                throw _failure;
            }

            return !_release.Wait(Patience)
                ? throw new TimeoutException("the parked digest was never released — the observation did not happen mid-digest")
                : _inner.Chunk(text, maxTokens, overlayTokens);
        }

        public void Arm() => _armed = true;

        public void FailWith(Exception failure) => _failure = failure;

        public void Release() => _release.Set();
    }
}
