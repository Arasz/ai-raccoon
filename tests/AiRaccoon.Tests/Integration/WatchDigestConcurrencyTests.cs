using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

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

    [Fact]
    public async Task TwoProcessesDigestingOnePath_KeepItSearchable_AndChunkItOnce()
    {
        var token = TestContext.Current.CancellationToken;
        using var bank = new Bank("digest-race");
        var one = bank.Process();
        var two = bank.Process();
        var reader = bank.Process();
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

    [Fact]
    public async Task DigestThatFailsMidIngest_LeavesThePreviousContentSearchable()
    {
        var token = TestContext.Current.CancellationToken;
        using var bank = new Bank("digest-crash");
        var one = bank.Process();
        var reader = bank.Process();
        await bank.SeedAsync(one, $"{Sentinel} v1body", token);

        bank.Write($"{Sentinel} v2body");
        one.Gate.Arm();
        one.Gate.FailWith(new IOException("ingest died mid-digest"));

        await Should.ThrowAsync<IOException>(() => bank.DigestAsync(one, token));

        (await bank.SearchAsync(reader, Sentinel, token))
            .ShouldNotBeEmpty("a digest that failed after the delete left the file with no chunks at all");
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

        public string DataRoot { get; }

        public string WatchDir { get; }

        public string FilePath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DataRoot, true);
            }
            catch (IOException)
            {
            }
        }

        public Process Process()
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

        public Task DigestAsync(Process process, CancellationToken cancellationToken) =>
            process.Executor.DigestAsync(Project, WatchDir, FilePath, WatchEventKind.Changed, null,
                cancellationToken);

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(Process process, string query,
            CancellationToken cancellationToken) =>
            process.Memory.SearchAsync(new SearchQuery(Project, query), cancellationToken);

        /// <summary>
        ///     What a search would retrieve for the file, read-only. The full SearchAsync also bumps
        ///     access counters, and that write would queue behind the digest's transaction rather
        ///     than report what is in the bank at this instant.
        /// </summary>
        public Task<int> RetrievableChunksAsync(Process process, string valueContains,
            CancellationToken cancellationToken) =>
            CountEntriesAsync(process, valueContains, cancellationToken);

        public async Task<int> CountEntriesAsync(Process process, string valueContains,
            CancellationToken cancellationToken)
        {
            await using var connection = await process.Factory.OpenBankAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT count(*) FROM entries WHERE project_id = @p AND source_file = @path AND value LIKE @value",
                new { p = Project, path = FilePath, value = $"%{valueContains}%" },
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
            Memory = new SqliteMemoryStore(Factory,
                NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(Factory), Gate, time, new EmbeddingService());
            WatchStore = new WatchStore(Factory);
            Executor = new WatchDigestExecutor(Memory, WatchStore, time,
                NullLogger<WatchDigestExecutor>.Instance);
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
    private sealed class GateChunker : IChunker
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IChunker _inner = new TokenizerChunker();

        private readonly ManualResetEventSlim _release = new(false);

        private bool _armed;

        private int _calls;

        private Exception? _failure;

        /// <summary>Chunk calls made while armed — one per full re-chunk of the file.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task Entered => _entered.Task;

        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0)
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

            if (!_release.Wait(Patience))
            {
                throw new TimeoutException("the parked digest was never released — the observation did not happen mid-digest");
            }

            return _inner.Chunk(text, maxTokens, overlayTokens);
        }

        public void Arm() => _armed = true;

        public void FailWith(Exception failure) => _failure = failure;

        public void Release() => _release.Set();
    }
}
