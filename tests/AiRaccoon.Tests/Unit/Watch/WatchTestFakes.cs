using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     Shared fakes + stack for the watch pipeline unit tests (docs/plans/file-watcher-implementation.md
///     Section S4). No real SQLite, no FileSystemWatcher.
/// </summary>
internal sealed class WatchTestStack
{
    public static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public WatchTestStack()
    {
        // The real store keeps the fingerprint inside the replace transaction; the fakes are wired
        // together here so the same re-check/write happens against the fake watch store.
        Memory.ReadFingerprint = Store.PeekFingerprint;
        Memory.WriteFingerprint = (projectId, path, hash) =>
            Store.SetFingerprint(projectId, path, hash, Time.GetUtcNow().ToUnixTimeSeconds());
        var scanInitiatorLazy = new Lazy<IWatchScanInitiator>(() => ScanInitiator);
        Executor = new WatchDigestExecutor(Memory, Store, Time, NullLogger<WatchDigestExecutor>.Instance,
            IgnoreRules, scanInitiatorLazy);
        Pipeline = new WatchPipeline(
            new WatchScheduler(), Executor, new WatchRetryPolicy(), ScanGuard,
            Memory, Time, NullLogger<WatchPipeline>.Instance);
        Service = new WatchService(Store, Memory, Pipeline, Time, OverlapResolver);
    }

    public FakeTimeProvider Time { get; } = new(FixedNow);

    public FakeWatchMemoryStore Memory { get; } = new();

    public FakeWatchStore Store { get; } = new();

    public WatchScanGuard ScanGuard { get; } = new();

    public FakeWatchScanLease ScanLease { get; } = new();

    public FakeIgnoreRulesProvider IgnoreRules { get; } = new();

    public FakeWatchScanInitiator ScanInitiator { get; } = new();

    public IWatchOverlapResolver OverlapResolver { get; } = new WatchOverlapResolver();

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
internal sealed class FakeWatchStore : IWatchStore, IWatchRegisteredStore
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

    /// <summary>Fails the whole reconcile pass — the loop's own failure path.</summary>
    public Exception? ListWatchesError { get; set; }

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

    /// <summary>The fake has no transaction to fail mid-way, and no real SQLite lock to serialize
    /// concurrent callers on; the real store's atomicity/TOCTOU-closure is proven against SQLite
    /// (WatchPruningTests), not this in-memory fake.</summary>
    public int ResolveAndAddCalls { get; private set; }

    public async Task<WatchOverlapDecision> ResolveAndAddAsync(string projectId, WatchOverlapCandidate candidate,
        IWatchOverlapResolver overlapResolver, CancellationToken cancellationToken = default)
    {
        ResolveAndAddCalls++;
        var existing = Watches
            .Where(w => w.Key.ProjectId == projectId)
            .Select(w => new WatchOverlapCandidate(w.Key.Path, w.Value.CreatedAt))
            .ToArray();
        var decision = overlapResolver.Resolve(existing, candidate);

        if (decision.Outcome == WatchOverlapOutcome.Accepted)
        {
            foreach (var pruned in decision.Pruned)
            {
                await RemoveWatchAsync(projectId, pruned.Path, cancellationToken).ConfigureAwait(false);
            }

            await AddWatchAsync(projectId, candidate.Path, candidate.CreatedAt, 0, cancellationToken)
                .ConfigureAwait(false);
        }

        return decision;
    }

    public Task<IReadOnlyList<WatchRegistration>> ListWatchesAsync(CancellationToken cancellationToken = default) =>
        ListWatchesError is not null
            ? throw ListWatchesError
            : Task.FromResult<IReadOnlyList<WatchRegistration>>(
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
        SetFingerprint(projectId, path, fileHash, updatedAt);
        return Task.CompletedTask;
    }

    public Task DeleteFileHashAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        RemoveFingerprint(projectId, path);
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
        return
        [
            .. FileHashes.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => k[(projectId.Length + 1)..])
        ];
    }

    /// <summary>Synchronous fingerprint read/write — the fake memory store's replace transaction uses these.</summary>
    public string? PeekFingerprint(string projectId, string path) => FileHashes.TryGetValue(Key(projectId, path), out var file) ? file.Hash : null;

    public void SetFingerprint(string projectId, string path, string fileHash, long updatedAt)
    {
        FileHashes[Key(projectId, path)] = (fileHash, updatedAt);
        UpsertFileHashCalls++;
    }

    /// <summary>Mirrors the real DeleteSourcePathAsync transaction: chunks + fingerprint die together.</summary>
    public void RemoveFingerprint(string projectId, string path) => FileHashes.Remove(Key(projectId, path));

    private static string Key(string projectId, string path) => $"{projectId}\u0000{path}";
}

/// <summary>IIgnoreRulesProvider fake: per-root rules, defaulting to IgnoreRules.Empty; records
/// every LoadAsync call's root so mid-scan re-read timing can be asserted.</summary>
internal sealed class FakeIgnoreRulesProvider : IIgnoreRulesProvider
{
    private readonly Dictionary<string, IgnoreRules> _rulesByRoot = new(IngestPath.PathComparer);
    private readonly Dictionary<string, IgnoreRules> _afterFirstCallByRoot = new(IngestPath.PathComparer);

    public List<string> LoadCalls { get; } = [];

    public void Set(string root, IgnoreRules rules) => _rulesByRoot[root] = rules;

    public void Set(string root, string content) => Set(root, IgnoreRules.Parse(content));

    /// <summary>
    ///     S5: a deterministic mid-scan edit, independent of any hook timing — the very first
    ///     <see cref="LoadAsync" /> call for <paramref name="root" /> returns <paramref name="before" />;
    ///     every call after that returns <paramref name="after" />, forever. Drives
    ///     <c>WatchCatchUp</c>'s own re-read-at-end-of-pass comparison to disagree exactly once, so a
    ///     genuine second pass is the only way the loop settles.
    /// </summary>
    public void SetTransition(string root, IgnoreRules before, IgnoreRules after)
    {
        _rulesByRoot[root] = before;
        _afterFirstCallByRoot[root] = after;
    }

    public Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        LoadCalls.Add(root);
        var current = _rulesByRoot.GetValueOrDefault(root, IgnoreRules.Empty);
        if (_afterFirstCallByRoot.TryGetValue(root, out var after))
        {
            _rulesByRoot[root] = after;
        }

        return Task.FromResult(current);
    }
}

/// <summary>IWatchScanInitiator fake: records every EnqueueInitialScan call.</summary>
internal sealed class FakeWatchScanInitiator : IWatchScanInitiator
{
    public List<(string ProjectId, string Path)> Calls { get; } = [];

    public void EnqueueInitialScan(string projectId, string path) => Calls.Add((projectId, path));
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

    public Task<bool> TryRenewAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.FromResult(RenewResults.Count > 0 ? RenewResults.Dequeue() : true);

    public Task ReleaseAsync(string projectId, string path, CancellationToken cancellationToken = default)
    {
        ReleaseCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>IMemoryStore fake: settings + the watch-used slice; the rest is unsupported.</summary>
internal sealed class FakeWatchMemoryStore : FakeMemoryStore
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

    /// <summary>Fingerprint re-check/write hooks — the fake's stand-in for the real replace transaction.</summary>
    public Func<string, string, string?>? ReadFingerprint { get; set; }

    public Action<string, string, string>? WriteFingerprint { get; set; }

    public override async Task<int> IngestFileAsync(string projectId, string path, string? context,
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

    /// <summary>Mirrors the real transaction: re-check the fingerprint, then delete, ingest and store it.</summary>
    public override async Task<bool> ReplaceIfFileChangedAsync(string projectId, string path, string fileHash,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(ReadFingerprint?.Invoke(projectId, path), fileHash, StringComparison.Ordinal))
        {
            return false;
        }

        await DeleteSourcePathAsync(projectId, path, cancellationToken);
        await IngestFileAsync(projectId, path, null, cancellationToken);
        WriteFingerprint?.Invoke(projectId, path, fileHash);
        return true;
    }

    public override Task<int> DeleteSourcePathAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            DeletedPaths.Add((projectId, path));
        }

        OnDeletePath?.Invoke(projectId, path);
        return Task.FromResult(0);
    }

    public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Settings.GetValueOrDefault(key));

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

    /// <summary>Projects the maintenance embed-retry sweep should walk (WP9).</summary>
    public List<string> ProjectIds { get; } = [];

    /// <summary>Pending-embed counts the sweep reads to decide whether to retry a project (WP9).</summary>
    public Dictionary<string, int> PendingCounts { get; } = [];

    public override Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MemoryStats(0, PendingCounts.GetValueOrDefault(projectId), []));

    public override Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(ProjectIds);

    public override Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
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
}
