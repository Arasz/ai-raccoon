using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests;

public static class TestData
{
    /// <summary>Serializes tests that mutate the process-global AIRACCOON_DB_PASSPHRASE (shared by CliCommandRunnerTests and ConfigCommandsEncryptionTests).</summary>
    public static readonly SemaphoreSlim EnvVarGate = new(1, 1);

    /// <summary>Builds a real <see cref="SqliteMemoryStore"/> wired to a <see cref="FileIngestor"/> backed by the given
    /// chunkers — the pre-DI-refactor convenience, kept as one place so tests stay decoupled from the ingest graph.</summary>
    public static SqliteMemoryStore CreateMemoryStore(
        ISqliteConnectionFactory factory,
        ILogger<SqliteMemoryStore> logger,
        IMemorySourceStore sourceStore,
        IMarkdownChunker markdownChunker,
        TimeProvider timeProvider,
        IEmbeddingService embeddings,
        IJsonChunker? jsonChunker = null,
        IEnumerable<INoiseFilterPolicy>? noisePolicies = null,
        IEnumerable<IAutoTtlPolicy>? ttlPolicies = null)
    {
        jsonChunker ??= RealJsonChunker(markdownChunker);
        var embedder = new EntryEmbedder(embeddings);
        var matcher = new FileTypeMatcher(
            [new MarkdownFileTypeHandler(markdownChunker), new JsonFileTypeHandler(jsonChunker)]);
        var fileIngestor = new FileIngestor(matcher, embedder, sourceStore, timeProvider);
        var noiseFilteringService = new NoiseFilteringService(noisePolicies ?? Array.Empty<INoiseFilterPolicy>(), null, timeProvider);
        return new SqliteMemoryStore(factory, sourceStore, fileIngestor, embedder, timeProvider, logger, noiseFilteringService, ttlPolicies ?? Array.Empty<IAutoTtlPolicy>());
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
        ServeCommands? serve = null) =>
        new(store, settings!, sync!, watch!, encryptionCommands!, extract!, maintenance!, serve!);

    /// <summary>A <see cref="ServerProbe"/> backed by a plain loopback HttpClient (the pre-DI-refactor ForLoopback shape).</summary>
    public static ServerProbe CreateServerProbe() => new(new LoopbackHttpClientFactory());

    /// <summary>Unreachable <see cref="IPromotionQueueStore"/> for command tests that never touch the queue (extract settings keys).</summary>
    internal static IPromotionQueueStore UnusedPromotionQueueStore() => new UnreachablePromotionQueueStore();

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

    public static InfrastructureOptions CreateInfrastructureOptions(string dataRoot, string rid = "osx-arm64") => new() { DataRoot = dataRoot, Rid = rid, Scope = InstallScope.User };

    /// <summary>BundledModel with a null logger and a factory that never opens real connections; the model copy beside the test host makes EnsureAsync return all-present.</summary>
    public static BundledModel CreateBundledModel() => new(NullLogger<BundledModel>.Instance, new NoopHttpClientFactory());

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

    private sealed class UnreachablePromotionQueueStore : IPromotionQueueStore
    {
        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PruneRejectedAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromotionQueueOrphanReport> PruneOrphansAsync(bool apply, CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

/// <summary>Recording fake for the propose tier — tool/hosted-service unit tests that must not touch a bank.</summary>
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

/// <summary>No-op implementation of <see cref="ISearchQualityService"/> for unit tests that construct <see cref="MemoryTools"/> directly.</summary>
public sealed class NoOpSearchQualityService : ISearchQualityService
{
    public Task RecordSearchAsync(string correlationId, string query, string? scope, string? projectId,
        string? sessionId, int resultCount, IReadOnlyList<string> topSourceFiles, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task RecordFollowThroughAsync(string correlationId, string filePath, CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordGradeAsync(string projectId, string correlationId, int grade, string? note,
        CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<SearchQualityMetrics> GetMetricsAsync(string? projectId, DateTimeOffset from,
        CancellationToken ct = default) =>
        Task.FromResult(new SearchQualityMetrics(0, 0, 0, 0, 0, 0, 0));
}

/// <summary>In-memory store fake for the shared-extraction path: settings, project ids, candidate rows and the shared index.</summary>
public sealed class FakeExtractionStore : IMemoryStore
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

    public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        ProjectListError is not null
            ? throw ProjectListError
            : Task.FromResult<IReadOnlyList<string>>(Projects);

    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
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

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) => Task.FromResult(Index);

    public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        Shared.Add((projectId, hash));
        var row = Candidates.Values.SelectMany(x => x).First(r => r.Hash == hash);
        Index = new SharedIndex(
            [.. Index.Values, row.Value],
            [.. Index.Paths, $"shared/{row.Path}"]);
        return Task.FromResult(new MemoryEntryResult(new MemoryEntry(hash, row.Path, ContextNaming.SharedContext, row.Value, 1), true));
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
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

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(Settings
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value!));

    public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        Settings.Remove(key);
        return Task.CompletedTask;
    }

    public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryEntryResult> AddContentAsync(string projectId, string path, string content, string? context,
        string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
