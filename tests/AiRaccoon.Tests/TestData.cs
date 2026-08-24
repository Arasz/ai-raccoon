using AiRaccoon.Infrastructure.Embedding.Manifest;
using System.Security.Cryptography;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Assets;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Projects;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests;

public static class TestData
{
    private const int DeleteTempRootMaxAttempts = 5;

    /// <summary>Serializes tests that mutate the process-global AIRACCOON_DB_PASSPHRASE (shared by CliCommandRunnerTests and ConfigCommandsEncryptionTests).</summary>
    public static readonly SemaphoreSlim EnvVarGate = new(1, 1);

    private static readonly TimeSpan DeleteTempRootFirstDelay = TimeSpan.FromMilliseconds(20);
    private static readonly IModelMigrationLease ModelMigrationLease = Substitute.For<IModelMigrationLease>();

    /// <summary>
    ///     Holds <see cref="EnvVarGate" /> as a READER, for a test that opens a bank through the real
    ///     host and therefore reads AIRACCOON_DB_PASSPHRASE without meaning to (docs/adr/0066). The
    ///     gate was built for writers; a reader overlapping one opens a plain bank with a key, which
    ///     SQLite reports as error 26, "file is not a database".
    /// </summary>
    public static async ValueTask<IAsyncDisposable> HoldEnvGateAsync(CancellationToken cancellationToken)
    {
        await EnvVarGate.WaitAsync(cancellationToken);
        return new EnvGateHold();
    }

    /// <summary>Builds a real <see cref="SqliteMemoryStore"/> wired to a <see cref="FileIngestor"/> backed by the given
    /// chunkers — the pre-DI-refactor convenience, kept as one place so tests stay decoupled from the ingest graph.
    /// A <paramref name="codeChunker"/> wires code-corpus support too (e.g. <see cref="NoOpCodeChunker"/> or
    /// <see cref="StubCodeChunker"/>); omitted, the store behaves as a memory-only bank, matching every
    /// existing caller.</summary>
    public static SqliteMemoryStore CreateMemoryStore(
        ISqliteConnectionFactory factory,
        ILogger<SqliteMemoryStore> logger,
        IMemorySourceStore sourceStore,
        IMarkdownChunker markdownChunker,
        TimeProvider timeProvider,
        IEmbeddingService embeddings,
        IModelMigrationLease? modelMigrationLease,
        IJsonChunker? jsonChunker,
        IEnumerable<INoiseFilterPolicy>? noisePolicies,
        ISettingsStore? settings,
        ICodeChunker? codeChunker,
        IIgnoreRulesProvider? ignoreRulesProvider,
        IMeasurementRecorder? measurements)
    {
        jsonChunker ??= RealJsonChunker(markdownChunker);
        var embedder = new EntryEmbedder(embeddings, modelMigrationLease ?? ModelMigrationLease, timeProvider);
        var matcher = new FileTypeMatcher(
            [new MarkdownFileTypeHandler(markdownChunker), new JsonFileTypeHandler(jsonChunker)]);
        // This helper's own documented contract is "omitted, the store behaves as a memory-only
        // bank" — a fixed no-op pump, never a caller override; a test that needs a real one
        // constructs FileIngestor/SqliteMemoryStore directly instead of through this convenience.
        IEventPump<EmbedDrainRequest> pump = NullEmbedDrainPump.Instance;
        var fileIngestor = codeChunker is null
            ? new FileIngestor(matcher, sourceStore, timeProvider, embeddings,
                ignoreRulesProvider ?? NullIgnoreRulesProvider.Instance, NullCodeFileTypeMatcher.Instance,
                NullCodeIngestor.Instance, NullWatchStore.Instance, pump)
            : new FileIngestor(matcher, sourceStore, timeProvider, embeddings,
                ignoreRulesProvider ?? NullIgnoreRulesProvider.Instance, new CodeFileTypeMatcher(),
                new CodeIngestor(new CodeFileTypeMatcher(), codeChunker, timeProvider), NullWatchStore.Instance, pump);
        var noiseFilteringService = new NoiseFilteringService(noisePolicies ?? []);
        return new SqliteMemoryStore(factory, sourceStore, fileIngestor, embedder, timeProvider, logger, noiseFilteringService,
            settings ?? new SqliteSettingsStore(factory), pump, measurements ?? NoOpMeasurementRecorder.Instance);
    }

    /// <summary>
    ///     Drains every embed-topic request currently queued the same way
    ///     <see cref="EmbedDrainService" />'s own consumer would — one <c>DrainOnceAsync</c> pass per
    ///     queued item — without starting the hosted service. The one place tests share this instead
    ///     of hand-rolling a drain loop per file (a write leaves rows <c>pending</c> and enqueues;
    ///     nothing embeds until this runs).
    /// </summary>
    public static async Task DrainEmbedTopicAsync(ISqliteConnectionFactory factory,
        IEventPump<EmbedDrainRequest> pump, IEntryEmbedder entryEmbedder, ICodeEmbedder? codeEmbedder = null,
        CancellationToken cancellationToken = default)
    {
        var service = new EmbedDrainService(pump, factory, entryEmbedder, codeEmbedder ?? new FakeCodeEmbedder(),
            new SqliteSettingsStore(factory), NoOpMeasurementRecorder.Instance, TimeProvider.System,
            TestTelemetry.None, NullLogger<EmbedDrainService>.Instance);
        foreach (var request in pump.DrainUpTo(int.MaxValue))
        {
            await service.DrainOnceAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     D4 fixture replacement for the deleted <c>ConfigureEmbeddingAsync</c> direct path: drives
    ///     the same seam `model set` uses in production — <see cref="IModelMigrationStore.StartModelMigrationAsync"/>
    ///     writes the engine settings and opens a migration row only when the engine actually
    ///     changed — then immediately drains it with a fresh <see cref="IEntryEmbedder"/>.
    ///     <see cref="IEntryEmbedder.DrainMigrationAsync"/> is a safe no-op when nothing is open, so
    ///     calling it unconditionally reproduces the old synchronous "configure and re-embed inline"
    ///     contract without a second, test-only config path. Pass <paramref name="clock"/> (the
    ///     fixture's own <c>FakeTimeProvider</c>) so the drain's lease and its <c>finished_at</c>
    ///     stamp share the fixture's simulated clock instead of real wall-clock time — omitting it
    ///     falls back to <see cref="TimeProvider.System"/>.
    /// </summary>
    public static async Task<EmbeddingConfig> ConfigureAndDrainEmbeddingAsync(IMemoryStore store,
        ISqliteConnectionFactory factory, IEmbeddingService embeddings, string provider, string? model,
        string? baseUrl, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        var config = await store.StartModelMigrationAsync(provider, model, baseUrl, cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var resolvedClock = clock ?? TimeProvider.System;
        var embedder = new EntryEmbedder(embeddings, new SqliteModelMigrationLease(resolvedClock), resolvedClock);
        await embedder.DrainMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
        return config;
    }

    /// <summary>Real o200k-backed markdown chunker for tests that exercise token bounds, not just structure.</summary>
    public static IMarkdownChunker RealMarkdownChunker()
    {
        var tokenizer = new O200kTokenizer();
        return new MarkdownChunker(tokenizer.CountTokens);
    }

    /// <summary>Real JSON chunker sharing one tokenizer with its markdown fallback.</summary>
    public static IJsonChunker RealJsonChunker(IMarkdownChunker? fallback = null)
    {
        var tokenizer = new O200kTokenizer();
        return new JsonFileTypeChunker(tokenizer.CountTokens, fallback ?? new MarkdownChunker(tokenizer.CountTokens),
            ChunkingDefaults.OverlayTokens);
    }

    /// <summary>Builds a <see cref="ConfigCommands"/> with only the sub-command(s) a test needs; unused sub-commands
    /// are null and never reached by the dispatcher for the verb the test runs.</summary>
    internal static ConfigCommands CreateConfigCommands(
        IMemoryStore store,
        SettingsCommands? settings = null,
        SyncCommands? sync = null,
        WatchCommands? watch = null,
        EncryptionCommands? encryptionCommands = null,
        ExtractCommands? extract = null,
        MaintenanceCommands? maintenance = null,
        PerformanceCommands? performance = null,
        ServeCommands? serve = null,
        NoiseEntriesCommands? noiseEntries = null,
        IModelMigrationStore? modelMigrations = null,
        ChunkIndexRepairCommands? chunkIndexRepair = null,
        ReingestRepairCommands? reingestRepair = null,
        DoctorCommands? doctor = null,
        ModelDownloadCommands? modelDownload = null,
        ICodeEngineStore? codeEngine = null) =>
        new(store, modelMigrations!, codeEngine!, settings!, sync!, watch!, encryptionCommands!, extract!, maintenance!, performance!, serve!, noiseEntries!, chunkIndexRepair!, reingestRepair!, doctor!, modelDownload!);

    /// <summary>A <see cref="ServerProbe"/> backed by a plain loopback HttpClient (the pre-DI-refactor ForLoopback shape).</summary>
    public static ServerProbe CreateServerProbe() => new(new LoopbackHttpClientFactory());

    /// <summary>Unreachable <see cref="IPromotionQueueStore"/> for command tests that never touch the queue (extract settings keys).</summary>
    internal static IPromotionQueueStore UnusedPromotionQueueStore() => new UnreachablePromotionQueueStore();

    /// <summary>Unreachable <see cref="IPromotionQueuePruneStore"/> for command tests that never touch prune (extract settings keys).</summary>
    internal static IPromotionQueuePruneStore UnusedPromotionQueuePruneStore() => new UnreachablePromotionQueuePruneStore();

    /// <summary>Resolves an <see cref="INodeRunner"/> from the real DI graph — serve tests start an actual HTTP host through it.</summary>
    public static INodeRunner CreateNodeRunner(InfrastructureOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(options);
        services.RegisterNodeServices();
        return services.BuildServiceProvider().GetRequiredService<INodeRunner>();
    }

    /// <summary>Resolves an <see cref="IObservabilityRunner"/> from the real DI graph.</summary>
    public static IObservabilityRunner CreateObservabilityRunner()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterNodeServices();
        return services.BuildServiceProvider().GetRequiredService<IObservabilityRunner>();
    }

    /// <summary>Resolves an <see cref="IProxyRunner"/> from the real DI graph.</summary>
    public static IProxyRunner CreateProxyRunner()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterProxyServices();
        return services.BuildServiceProvider().GetRequiredService<IProxyRunner>();
    }

    public static string CreateTempRoot(string prefix = "ai-raccoon-tests")
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Idempotent, retry-tolerant teardown for a <see cref="CreateTempRoot"/> directory: a
    /// directory already gone is success (not a spurious Dispose failure), a transient lock — a
    /// handle not yet closed, a scan racing teardown — gets a few short retries. A lock that never
    /// clears still throws after the bound: swallowing it would hide a real leak behind a green test.</summary>
    public static void DeleteTempRoot(string path)
    {
        var delay = DeleteTempRootFirstDelay;
        for (var attempt = 1;; attempt++)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (attempt < DeleteTempRootMaxAttempts &&
                                       ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(delay);
                delay += delay;
            }
        }
    }

    public static InfrastructureOptions CreateInfrastructureOptions(string dataRoot, string rid = "osx-arm64") => new() { DataRoot = dataRoot, Rid = rid, Scope = InstallScope.User };

    /// <summary>The embed topic's pump, built with production's own shape (WP11-B2) — a fresh
    /// instance per test so pump counters never leak between tests.</summary>
    public static IEventPump<EmbedDrainRequest> NewEmbedDrainPump() =>
        new EventPump<EmbedDrainRequest>(new PumpTopic(EmbedDrainService.PumpCeiling, EmbedDrainService.PumpCapacity, Coalesce: true));

    /// <summary>BundledModel with a null logger and a factory that never opens real connections; the model copy beside the test host makes EnsureAsync return all-present.</summary>
    public static BundledModel CreateBundledModel() => new(NullLogger<BundledModel>.Instance, new NoopHttpClientFactory());

    /// <summary>EmbeddingService with a null logger — the constructor requires a real <see cref="ILogger{TCategoryName}"/> now that it is DI-registered, so tests that don't care about logging use this.</summary>
    public static EmbeddingService CreateEmbeddingService() => new(NullLogger<EmbeddingService>.Instance, new LocalTokenizer(),
        new EmbeddingTokenizerFactory(), new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
        NoOpMeasurementRecorder.Instance, TimeProvider.System);

    /// <summary>
    ///     Bootstraps the pinned sentencepiece fixture (tests/AiRaccoon.Tests/Resources/tokenizers/manifest.json)
    ///     into the scratch dir, sha-verified, mirroring the Retrieval-assets precedent: the pin is
    ///     committed, the 5 MB binary is not. Returns the model file path.
    /// </summary>
    public static async Task<string> EnsureSentencePieceFixtureAsync(CancellationToken cancellationToken)
    {
        var scratchDir = Path.Combine(Path.GetTempPath(), "ai-raccoon-test-fixtures");
        var target = Path.Combine(scratchDir, "xlm-roberta-sentencepiece.bpe.model");
        if (File.Exists(target) && BundledResource.Sha256Of(target)
                .Equals(SentencePieceFixtureSha256, StringComparison.OrdinalIgnoreCase))
        {
            return target;
        }

        Directory.CreateDirectory(scratchDir);
        var bytes = await new AssetDownloader(new HttpClient()).GetAsync(SentencePieceFixtureUrl, cancellationToken)
            .ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!actual.Equals(SentencePieceFixtureSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"sentencepiece fixture download failed sha verification: expected {SentencePieceFixtureSha256}, got {actual}");
        }

        await File.WriteAllBytesAsync(target, bytes, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public const string SentencePieceFixtureUrl =
        "https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/sentencepiece.bpe.model";

    public const string SentencePieceFixtureSha256 =
        "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";

    /// <summary>Returns the p-th percentile (0–1) of the samples.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double quantile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var sorted = samples.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(quantile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Cosine similarity between two float vectors.</summary>
    public static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var aSpan = a.Span;
        var bSpan = b.Span;
        double dot = 0;
        for (var i = 0; i < aSpan.Length; i++)
        {
            dot += aSpan[i] * bSpan[i];
        }

        return dot;
    }

    /// <summary>A real <see cref="IEmbeddingManifestLoader" /> — no fake: the B1 activation gate
    /// (§3.3 D-E9) depends on the loader's own validation, not a stand-in.</summary>
    public static IEmbeddingManifestLoader CreateManifestLoader() =>
        new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator());

    /// <summary>Stages the code-daemon-embed-v1 fixture manifest (768-dim, 128-ctx) into
    /// <paramref name="dir" /> with stub tokenizer/onnx files, so a real
    /// <see cref="IEmbeddingManifestLoader" /> accepts it — for tests that activate a real code
    /// engine and need the B1 manifest gate (§3.3 D-E9) to pass.</summary>
    public static void SeedCodeManifestDirectory(string dir) => SeedCodeManifestDirectory(dir, CodeCorpusSchema.EmbeddingDimensions);

    /// <summary>Same seed at an explicit dimension: rewrites the 768 fixture's `dimensions` line
    /// (contextWindowTokens stays 512, above the chunk budget, so only the dimension differs —
    /// the vec-code-unfix-dim tests need a non-768 manifest that the chunk gate still accepts).</summary>
    public static void SeedCodeManifestDirectory(string dir, int dimensions)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "sentencepiece.bpe.model"), "tokenizer");
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "model");
        var manifest = File.ReadAllText(
            RepoFile("tests/AiRaccoon.Tests/Resources/ManifestFixtures/code-daemon-embed-v1.json"));
        File.WriteAllText(Path.Combine(dir, EmbeddingManifest.FileName),
            manifest.Replace("\"dimensions\": 768,", $"\"dimensions\": {dimensions},", StringComparison.Ordinal));
    }

    /// <summary>Rewrites a seeded manifest's pooling block — the shape `model download` wrote
    /// before it read the graph's output rank (#470): a token-level mode plus the output name.
    /// <paramref name="embeddingOutput" /> names a second, distinctly-named output when set (the
    /// #497 pre-#496 bge-m3 shape: the planner always name-selected this field independent of
    /// pooling mode); omitted, it reproduces the #470 sole-output shape.</summary>
    public static void WriteManifestPooling(string dir, PoolingMode mode, string tokenEmbeddingsOutput, string? embeddingOutput = null)
    {
        var serializer = new EmbeddingManifestSerializer();
        var path = Path.Combine(dir, EmbeddingManifest.FileName);
        var manifest = serializer.Deserialize(File.ReadAllText(path));
        File.WriteAllText(path, serializer.Serialize(manifest with
        {
            Pooling = new PoolingManifest(mode, new PoolingOutputNames(embeddingOutput ?? string.Empty, tokenEmbeddingsOutput)),
            Onnx = manifest.Onnx with { EmbeddingOutput = embeddingOutput, TokenEmbeddingsOutput = tokenEmbeddingsOutput }
        }));
    }

    /// <summary>A real <see cref="IManifestPoolingRepair" /> over a stand-in graph; the default
    /// graph declares no outputs, so a manifest that already tells the truth is left alone.</summary>
    public static IManifestPoolingRepair CreateManifestPoolingRepair(
        IOnnxSmokeTester? graph = null, ILogger<ManifestPoolingRepair>? logger = null) =>
        new ManifestPoolingRepair(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator(),
            graph ?? GraphWithOutputRanks(), logger ?? NullLogger<ManifestPoolingRepair>.Instance);

    /// <summary>An ONNX graph that loads and declares the given output ranks — the injectable half
    /// of every rank-driven decision (#470), so no test needs a real multi-hundred-MB graph.</summary>
    public static IOnnxSmokeTester GraphWithOutputRanks(params (string Output, int Rank)[] ranks) =>
        new FakeGraph(ranks.ToDictionary(r => r.Output, r => r.Rank, StringComparer.Ordinal));

    private sealed class FakeGraph(IReadOnlyDictionary<string, int> ranks) : IOnnxSmokeTester
    {
        public IReadOnlyDictionary<string, int> Verify(string onnxPath) => ranks;
    }

    /// <summary>Locates a repo-relative file by walking up from the test output directory.</summary>
    public static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {relative} from the test output directory.");
    }

    private sealed class EnvGateHold : IAsyncDisposable
    {
        private bool _released;

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                EnvVarGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnreachablePromotionQueueStore : IPromotionQueueStore
    {
        public Task<int> PurgeOldDiscardsAsync(long nowUnixSeconds, int retentionDays, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueRow?> ClaimAsync(string projectId, string hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PruneRejectedAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueOrphanReport> PruneOrphansAsync(bool apply, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnreachablePromotionQueuePruneStore : IPromotionQueuePruneStore
    {
        public Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LoopbackHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = ServerProbe.RequestTimeout };
    }

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>Recording fake for the proposal tier — tool/hosted-service unit tests that must not touch a bank.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public sealed class FakePromotionQueue : IPromotionQueue
{
    public string? LastProject { get; private set; }
    public IReadOnlyList<QueueCandidate>? LastCandidates { get; private set; }
    public IReadOnlyList<string>? LastPromoteProjects { get; private set; }
    public List<IReadOnlyList<string>> PromoteCalls { get; } = [];
    public int? LastLimit { get; private set; }
    public PromoteOutcome PromoteOutcome { get; set; } = new([], 0, new Dictionary<string, int>());
    public Exception? PromoteError { get; set; }
    public Exception? GetMetaError { get; set; }
    public TimeSpan GetMetaDelay { get; set; }
    public PromotionMeta Meta { get; set; } = new(0, null);

    public string? LastDiscardProject { get; private set; }
    public string? LastDiscardHash { get; private set; }
    public int DiscardResult { get; set; }

    public IReadOnlyList<PromotionQueueRow> Rows { get; set; } = [];
    public string? LastListProject { get; private set; }
    public int? LastListLimit { get; private set; }

    public string? LastMetaProject { get; private set; }
    public bool MetaAsked { get; private set; }

    /// <summary>(ProjectId, CurrentScorerVersion) for every ClearStaleAsync call, in order.</summary>
    public List<(string ProjectId, int CurrentScorerVersion)> ClearStaleCalls { get; } = [];

    public Task<ProposeOutcome> ProposeAsync(string projectId, IReadOnlyList<QueueCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        LastProject = projectId;
        LastCandidates = candidates;
        return Task.FromResult(new ProposeOutcome(candidates.Count, []));
    }

    public Task<PromoteOutcome> PromoteAsync(IReadOnlyList<string> projectIds, int limit,
        CancellationToken cancellationToken = default)
    {
        LastPromoteProjects = projectIds;
        PromoteCalls.Add(projectIds);
        LastLimit = limit;
        if (PromoteError is not null)
        {
            throw PromoteError;
        }

        return Task.FromResult(PromoteOutcome);
    }

    public Task<int> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default)
    {
        LastDiscardProject = projectId;
        LastDiscardHash = hash;
        return Task.FromResult(DiscardResult);
    }

    public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, int limit,
        CancellationToken cancellationToken = default)
    {
        LastListProject = projectId;
        LastListLimit = limit;
        return Task.FromResult<IReadOnlyList<PromotionQueueRow>>([.. Rows.Take(limit)]);
    }

    /// <summary>Simulates the real store: removes this project's rows off-version from <see cref="Rows"/> so
    /// a later ListAsync in the same pass reflects the clear, the way SharedExtractionRunner depends on.</summary>
    public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
        CancellationToken cancellationToken = default)
    {
        ClearStaleCalls.Add((projectId, currentScorerVersion));
        var before = Rows.Count;
        Rows = [.. Rows.Where(r => r.ProjectId != projectId || r.ScorerVersion == currentScorerVersion)];
        return Task.FromResult(before - Rows.Count);
    }

    public async Task<PromotionMeta> GetMetaAsync(string? projectId, CancellationToken cancellationToken = default)
    {
        LastMetaProject = projectId;
        MetaAsked = true;
        if (GetMetaDelay > TimeSpan.Zero)
        {
            await Task.Delay(GetMetaDelay, cancellationToken);
        }

        if (GetMetaError is not null)
        {
            throw GetMetaError;
        }

        return Meta;
    }
}

/// <summary>Stub <see cref="IModelMigrationStore"/> for tests that construct a <see cref="ToolGate"/> directly and do not exercise ADR-0076's migration lock.</summary>
public sealed class NeverMigratingStore : IModelMigrationStore
{
    public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not exercised by ToolGate.");
}

/// <summary>Stub <see cref="IProjectRegistrationGuard"/> for tests that construct a <see cref="ToolGate"/> directly and do not exercise ADR-0089's registration refusal.</summary>
public sealed class AllowingRegistrationGuard : IProjectRegistrationGuard
{
    public Task EnsureAsync(string projectId, AccessRequirement requirement, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>No-op implementation of <see cref="ISearchQualityService"/> for unit tests that construct <see cref="MemoryTools"/> directly.</summary>
public sealed class NoOpSearchQualityService : ISearchQualityService
{
    public Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
        string? sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RecordSearchSafeAsync(string correlationId, string query, string? scope, string? projectId,
        int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RecordFollowThroughAsync(string correlationId, string filePath, CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from,
        CancellationToken ct = default) =>
        Task.FromResult(new SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));
}

/// <summary>Empty-results implementation of <see cref="ICodeSearchService"/> for unit tests that construct <see cref="MemoryTools"/> directly and do not exercise kind=code/both.</summary>
public sealed class NoOpCodeSearchService : ICodeSearchService
{
    public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new CodeSearchResults([]));

    public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
        Task.FromResult<CodeEntry?>(null);
}

/// <summary>Discards every measurement — for tests that need an <see cref="IMeasurementRecorder" /> but do not assert on it.</summary>
public sealed class NoOpMeasurementRecorder : IMeasurementRecorder
{
    public static NoOpMeasurementRecorder Instance { get; } = new();

    public void Record(Measurement measurement)
    {
    }
}

/// <summary>Captures every measurement handed to it; optionally throws, to exercise a caller's best-effort handling.</summary>
public sealed class RecordingMeasurementRecorder : IMeasurementRecorder
{
    public List<Measurement> Recorded { get; } = [];

    public Exception? ThrowOnRecord { get; set; }

    /// <summary>Every call, whether it throws or not — scenario 19's "not retried" seam.</summary>
    public int CallCount { get; private set; }

    public void Record(Measurement measurement)
    {
        CallCount++;
        if (ThrowOnRecord is { } exception)
        {
            throw exception;
        }

        Recorded.Add(measurement);
    }
}

/// <summary>In-memory store fake for the shared-extraction path: settings, project ids, candidate rows and the shared index.</summary>
public sealed class FakeExtractionStore : FakeMemoryStore
{
    private int _intervalReads;
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

    public List<string> Projects { get; } = ["acme", "beta"];

    public Dictionary<string, List<ExtractionCandidateRow>> Candidates { get; } = new();

    public SharedIndex Index { get; set; } = new([], []);

    public List<(string ProjectId, string Hash)> Shared { get; } = [];

    public Exception? ExtractionError { get; set; }

    public int ExtractionCalls { get; private set; }

    public Exception? IntervalReadError { get; set; }

    /// <summary>Fails the whole pass rather than one project — the loop's own failure path.</summary>
    public Exception? ProjectListError { get; set; }

    /// <summary>Interval reads seen — the loop reads it once before creating its timer.</summary>
    public int IntervalReads => _intervalReads;

    public override Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        ProjectListError is not null
            ? throw ProjectListError
            : Task.FromResult<IReadOnlyList<string>>(Projects);

    public override Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default)
    {
        ExtractionCalls++;
        if (ExtractionError is not null && projectId == "acme")
        {
            throw ExtractionError;
        }

        return Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>(
            Candidates.GetValueOrDefault(projectId) ?? []);
    }

    public override Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) => Task.FromResult(Index);

    public override Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        Shared.Add((projectId, hash));
        var row = Candidates.Values.SelectMany(x => x).First(r => r.Hash == hash);
        Index = new SharedIndex(
            [.. Index.Values, row.Value],
            [.. Index.Paths, $"shared/{row.Path}"]);
        return Task.FromResult(new MemoryEntryResult(new MemoryEntry(hash, row.Path, ContextNaming.SharedContext, row.Value, 1), true));
    }

    public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key == ExtractionConfigKeys.IntervalMinutesGlobal)
        {
            Interlocked.Increment(ref _intervalReads);
        }

        if (IntervalReadError is not null && key == ExtractionConfigKeys.IntervalMinutesGlobal)
        {
            throw IntervalReadError;
        }

        return Task.FromResult(Settings.GetValueOrDefault(key));
    }

    public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(Settings
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!));

    public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Settings.Remove(key);
        return Task.CompletedTask;
    }
}
