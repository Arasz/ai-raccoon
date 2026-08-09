using AiRaccoon.Core;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiRaccoon.Tests;

public static class TestData
{
    /// <summary>Serializes tests that mutate the process-global AIRACCOON_DB_PASSPHRASE (shared by CliCommandRunnerTests and ConfigCommandsEncryptionTests).</summary>
    public static readonly SemaphoreSlim EnvVarGate = new(1, 1);

    public static string CreateTempRoot(string prefix = "ai-raccoon-tests")
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static InfrastructureOptions CreateInfrastructureOptions(string dataRoot, string rid = "osx-arm64") =>
        new() { DataRoot = dataRoot, Rid = rid, Scope = InstallScope.User };

    /// <summary>BundledModel with a null logger and a factory that never opens real connections; the model copy beside the test host makes EnsureAsync return all-present.</summary>
    public static BundledModel CreateBundledModel() => new(NullLogger<BundledModel>.Instance, new NoopHttpClientFactory());

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

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

    public string? LastDiscardProject { get; private set; }
    public string? LastDiscardHash { get; private set; }
    public int DiscardResult { get; set; }

    public Task<int> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default)
    {
        LastDiscardProject = projectId;
        LastDiscardHash = hash;
        return Task.FromResult(DiscardResult);
    }

    public IReadOnlyList<PromotionQueueRow> Rows { get; set; } = [];
    public string? LastListProject { get; private set; }
    public int? LastListLimit { get; private set; }

    public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, int limit,
        CancellationToken cancellationToken = default)
    {
        LastListProject = projectId;
        LastListLimit = limit;
        return Task.FromResult<IReadOnlyList<PromotionQueueRow>>(Rows.Take(limit).ToList());
    }

    public string? LastMetaProject { get; private set; }
    public bool MetaAsked { get; private set; }

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

/// <summary>In-memory store fake for the shared-extraction path: settings, project ids, candidate rows and the shared index.</summary>
public sealed class FakeExtractionStore : IMemoryStore
{
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

    private int _intervalReads;

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

    public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        Shared.Add((projectId, hash));
        var row = Candidates.Values.SelectMany(x => x).First(r => r.Hash == hash);
        Index = new SharedIndex(
            Index.Values.Append(row.Value).ToArray(),
            Index.Paths.Append($"shared/{row.Path}").ToArray());
        return Task.FromResult(new MemoryEntry(hash, row.Path, ContextNaming.SharedContext, row.Value, 1));
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

    public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
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
