using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Watch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Shared fakes + stack for the S4 pipeline unit tests (no real SQLite, no FileSystemWatcher).</summary>
internal sealed class WatchTestStack
{
    public static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public WatchTestStack()
    {
        var host = new MemoryExtensionHost(Memory, [Extension]);
        Executor = new WatchDigestExecutor(host, Store, host, Time, NullLogger<WatchDigestExecutor>.Instance);
        Pipeline = new WatchPipeline(
            new WatchScheduler(), Executor, new WatchRetryPolicy(), Memory, Time, ScanGuard,
            NullLogger<WatchPipeline>.Instance);
        Service = new WatchService(Store, Memory, Pipeline, Time);
    }

    public FakeTimeProvider Time { get; } = new(FixedNow);

    public FakeMemoryStore Memory { get; } = new();

    public FakeWatchStore Store { get; } = new();

    public RecordingExtension Extension { get; } = new();

    public WatchScanGuard ScanGuard { get; } = new();

    public FakeWatchScanLease ScanLease { get; } = new();

    public WatchDigestExecutor Executor { get; }

    public WatchPipeline Pipeline { get; }

    public WatchService Service { get; }

    public void Enable(string projectId = "acme") => Memory.Settings[WatchConfigKeys.EnabledProject(projectId)] = "true";

    public void AllowScope(string path, string projectId = "acme") => Memory.Settings[IngestScopeKeys.ScopeProject(projectId)] = IngestScopeKeys.Serialize([path]);
}

/// <summary>Unique disposable temp directory for digest tests (files must exist on disk for the executor).</summary>
internal sealed class TempDir : IDisposable
{
    private TempDir(string root)
    {
        Path = System.IO.Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }

    public static TempDir New(string name) => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ai-raccoon-watch-tests", name));

    public string File(string name) => System.IO.Path.Combine(Path, name);
}

/// <summary>In-memory IWatchStore: watches + per-file fingerprints, with call counters.</summary>
internal sealed class FakeWatchStore : IWatchStore
{
    private static readonly WatchKeyComparer KeyComparer = WatchKeyComparer.Instance;

    public Dictionary<(string ProjectId, string Path), (long CreatedAt, long LastChangeTs)> Watches { get; } =
        new(KeyComparer);

    public Dictionary<string, (string Hash, long UpdatedAt)> FileHashes { get; } = new(StringComparer.Ordinal);

    public int AddWatchCalls { get; private set; }

    public int RemoveWatchCalls { get; private set; }

    public int UpdateLastChangeCalls { get; private set; }

    public int UpsertFileHashCalls { get; private set; }

    public int ListFilesCalls { get; private set; }

    /// <summary>Tokens passed to each ListFilesAsync call, in call order (asserts cancellation threading).</summary>
    public List<CancellationToken> ListFilesTokens { get; } = [];

    /// <summary>Runs before ListFilesAsync returns — gate to hold a scan open inside ReconcileMissingAsync.</summary>
    public Func<Task>? OnListFiles { get; set; }

    public Task AddWatchAsync(string projectId, string path, long createdAt, long lastChangeTs,
        CancellationToken cancellationToken = default)
    {
        Watches.TryAdd((projectId, path), (createdAt, lastChangeTs));
        AddWatchCalls++;
        return Task.CompletedTask;
    }

    /// <summary>Mirrors the real cascade delete: fingerprints at or under the watch path die with it.</summary>
    public Task RemoveWatchAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        Watches.Remove((projectId, path));
        foreach (var key in FileHashes.Keys
                     .Where(k => k.StartsWith($"{projectId}\u0000", StringComparison.Ordinal) &&
                                 IngestPath.IsWithinScope(k[(projectId.Length + 1)..], path))
                     .ToArray())
        {
            FileHashes.Remove(key);
        }

        RemoveWatchCalls++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WatchRegistration>>(
            [.. Watches.Select(w => new WatchRegistration(w.Key.ProjectId, w.Key.Path, w.Value.CreatedAt, w.Value.LastChangeTs))]);

    public Task UpdateLastChangeAsync(string projectId, string path, long lastChangeTs,
        CancellationToken cancellationToken = default)
    {
        UpdateLastChangeCalls++;
        if (Watches.TryGetValue((projectId, path), out var watch))
        {
            Watches[(projectId, path)] = (watch.CreatedAt, lastChangeTs);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetFileHashAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(FileHashes.TryGetValue(Key(projectId, path), out var file) ? file.Hash : null);

    public Task UpsertFileHashAsync(string projectId, string path, string fileHash, long updatedAt,
        CancellationToken cancellationToken = default)
    {
        FileHashes[Key(projectId, path)] = (fileHash, updatedAt);
        UpsertFileHashCalls++;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ListFilesCalls++;
        ListFilesTokens.Add(cancellationToken);
        if (OnListFiles is not null)
        {
            await OnListFiles();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var prefix = $"{projectId}\u0000";
        return [
            .. FileHashes.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[(projectId.Length + 1)..])
        ];
    }

    /// <summary>Mirrors the real DeleteSourcePathAsync transaction: chunks + fingerprint die together.</summary>
    public void RemoveFingerprint(string projectId, string path) => FileHashes.Remove(Key(projectId, path));

    private static string Key(string projectId, string path) => $"{projectId}\u0000{path}";
}

/// <summary>IWatchScanLease fake: grants by default, with injectable results and call counters.</summary>
internal sealed class FakeWatchScanLease : IWatchScanLease
{
    public bool AcquireResult { get; set; } = true;

    public Queue<bool> RenewResults { get; } = new();

    public int ReleaseCalls { get; private set; }

    public Action? OnAcquire { get; set; }

    public Task<bool> TryAcquireAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        OnAcquire?.Invoke();
        return Task.FromResult(AcquireResult);
    }

    public Task<bool> TryRenewAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(RenewResults.Count > 0 ? RenewResults.Dequeue() : true);

    public Task ReleaseAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        ReleaseCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>IMemoryStore fake: settings + the watch-used slice; the rest is unsupported.</summary>
internal sealed class FakeMemoryStore : IMemoryStore
{
    private readonly object _sync = new();
    public Dictionary<string, string?> Settings { get; } = new(StringComparer.Ordinal);

    // Scheduler jobs run concurrently (Task.Run, concurrency 4) — plain lists would race.
    public List<(string ProjectId, string Path, string Content)> Ingested { get; } = [];

    public List<(string ProjectId, string Path)> DeletedPaths { get; } = [];

    /// <summary>When set, IngestFileAsync throws it (digest failure injection).</summary>
    public Exception? IngestError { get; set; }

    /// <summary>When set, EmbedPendingAsync throws it (best-effort embed failure injection).</summary>
    public Exception? EmbedError { get; set; }

    /// <summary>Project ids passed to EmbedPendingAsync, in call order.</summary>
    public List<string> EmbedCalls { get; } = [];

    /// <summary>Runs after a successful ingest records content — TCS gating for in-flight digests.</summary>
    public Func<string, Task>? OnIngest { get; set; }

    /// <summary>Mirrors the real transaction: DeleteSourcePathAsync also drops the fingerprint.</summary>
    public Action<string, string>? OnDeletePath { get; set; }

    public TaskCompletionSource FirstIngestTcs { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        if (IngestError is not null)
        {
            throw IngestError;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        lock (_sync)
        {
            Ingested.Add((projectId, path, content));
        }

        FirstIngestTcs.TrySetResult();
        if (OnIngest is not null)
        {
            await OnIngest(path);
        }

        return 1;
    }

    public Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            DeletedPaths.Add((projectId, path));
        }

        OnDeletePath?.Invoke(projectId, path);
        return Task.FromResult(0);
    }

    public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Settings.GetValueOrDefault(key));

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


    public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

    public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            EmbedCalls.Add(projectId);
        }

        if (EmbedError is not null)
        {
            throw EmbedError;
        }

        return Task.FromResult(new EmbedPendingResult(0, 0));
    }

    public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
        string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}

/// <summary>IMemoryExtension fake: records OnSourceChangedAsync dispatches, can be made to throw.</summary>
internal sealed class RecordingExtension : IMemoryExtension
{
    public List<SourceChangedContext> SourceChanges { get; } = [];

    public bool ThrowOnSourceChanged { get; set; }
    public string Name => "Recording";

    public Task OnSourceChangedAsync(SourceChangedContext context, CancellationToken cancellationToken)
    {
        if (ThrowOnSourceChanged)
        {
            throw new InvalidOperationException("hook boom");
        }

        SourceChanges.Add(context);
        return Task.CompletedTask;
    }

    public Task OnWriteAsync(WriteContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnSearchAsync(SearchContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnDeleteAsync(DeleteContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
