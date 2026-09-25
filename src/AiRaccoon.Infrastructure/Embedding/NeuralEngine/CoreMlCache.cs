using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Embedding;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Embedding.NeuralEngine;

/// <summary>
///     The compiled CoreML models under <c>&lt;root&gt;/&lt;key&gt;/&lt;ORT version&gt;/</c>, one directory per bucket
///     (ADR-0118). A lock file there is held exclusively while a process compiles and shared while it
///     serves, so processes never delete or half-read each other's sets.
/// </summary>
internal sealed partial class CoreMlCache
{
    public const string LockFileName = ".lock";
    public const string CompleteMarkerName = ".complete";
    public const string StatusFileName = "status.json";
    private const string ComputeUnitsDirectory = "CPUAndNeuralEngine";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _root;
    private readonly string _key;
    private readonly string _ortVersion;
    private readonly ILogger _logger;

    public CoreMlCache(string root, string key, string ortVersion, ILogger logger)
    {
        Guard.IsNotNullOrWhiteSpace(root);
        Guard.IsNotNullOrWhiteSpace(key);
        Guard.IsNotNullOrWhiteSpace(ortVersion);
        Guard.IsNotNull(logger);
        _root = Path.GetFullPath(root);
        _key = key;
        _ortVersion = ortVersion;
        _logger = logger;
    }

    private string SetDirectory => Path.Combine(_root, _key, _ortVersion);

    /// <summary>The cache key: the first 12 hex digits of sha256 over the graph's sha and the pinned weights sha.</summary>
    public static string KeyFor(string graphSha256, string weightsSha256)
    {
        Guard.IsNotNullOrWhiteSpace(graphSha256);
        Guard.IsNotNullOrWhiteSpace(weightsSha256);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(graphSha256.ToLowerInvariant() + weightsSha256.ToLowerInvariant()));
        return Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>Where the session for <paramref name="bucket" /> caches its compiled model.</summary>
    public string BucketDirectory(int bucket) => Path.Combine(SetDirectory, ComputeUnitsDirectory, $"bucket-{bucket}");

    /// <summary>
    ///     Locks this set for loading: shared when a finished set exists (warm), otherwise exclusive
    ///     after deleting any half-written set (cold). Waits while another process compiles it.
    /// </summary>
    public CoreMlCacheLease Acquire(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SetDirectory);
        var marker = Path.Combine(SetDirectory, CompleteMarkerName);
        var waiting = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(marker) && TryLock(SetDirectory, FileShare.Read) is { } shared)
            {
                return new CoreMlCacheLease(this, shared, false);
            }

            if (!File.Exists(marker) && TryLock(SetDirectory, FileShare.None) is { } exclusive)
            {
                if (File.Exists(marker))
                {
                    exclusive.Dispose();
                    continue;
                }

                DeleteIfPresent(Path.Combine(SetDirectory, ComputeUnitsDirectory));
                return new CoreMlCacheLease(this, exclusive, true);
            }

            if (!waiting)
            {
                waiting = true;
                Log.WaitingForOtherProcess(_logger, SetDirectory);
            }

            cancellationToken.WaitHandle.WaitOne(PollInterval);
        }
    }

    /// <summary>Deletes every other key and ORT version's set that no process holds, never following a path out of the cache root.</summary>
    public void Prune()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        foreach (var keyDirectory in Directory.EnumerateDirectories(_root))
        {
            if (!IsInsideRoot(keyDirectory))
            {
                continue;
            }

            try
            {
                var isCurrentKey = Path.GetFileName(keyDirectory) == _key;
                foreach (var set in Directory.EnumerateDirectories(keyDirectory))
                {
                    if (!(isCurrentKey && Path.GetFileName(set) == _ortVersion))
                    {
                        PruneSet(set);
                    }
                }

                if (!isCurrentKey && !Directory.EnumerateFileSystemEntries(keyDirectory).Any())
                {
                    Directory.Delete(keyDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.WriteFailed(_logger, "delete a stale cache set in", keyDirectory, ex.Message);
            }
        }
    }

    /// <summary>Total bytes of every file under the cache root; 0 when there is none.</summary>
    public long SizeBytes() =>
        Directory.Exists(_root)
            ? new DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
            : 0;

    /// <summary>Replaces <c>status.json</c> with <paramref name="transition" />, atomically; a failed write is logged, never thrown.</summary>
    public void WriteStatus(NeuralEngineTransition transition)
    {
        Guard.IsNotNull(transition);
        var path = Path.Combine(_root, StatusFileName);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(_root);
            using (var stream = File.Create(temporary))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("state", transition.To.ToString());
                writer.WriteString("trigger", transition.Trigger.ToString());
                writer.WriteString("reason", transition.Reason);
                writer.WriteString("at", transition.At);
                writer.WriteNumber("pid", Environment.ProcessId);
                writer.WriteEndObject();
            }

            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.WriteFailed(_logger, "write the status file", path, ex.Message);
        }
    }

    internal void WriteMarker() => File.WriteAllText(Path.Combine(SetDirectory, CompleteMarkerName), "");

    /// <summary>A shared lock on this set, or null when it cannot be retaken within a few seconds.</summary>
    internal FileStream? LockShared()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (TryLock(SetDirectory, FileShare.Read) is { } shared)
            {
                return shared;
            }

            Thread.Sleep(PollInterval);
        }

        return null;
    }

    private void PruneSet(string set)
    {
        if (!IsInsideRoot(set))
        {
            return;
        }

        using (var exclusive = TryLock(set, FileShare.None))
        {
            if (exclusive is null)
            {
                return;
            }

            foreach (var entry in new DirectoryInfo(set).EnumerateFileSystemInfos())
            {
                if (entry.Name == LockFileName)
                {
                    continue;
                }

                if (entry is DirectoryInfo directory && directory.LinkTarget is null)
                {
                    directory.Delete(true);
                }
                else
                {
                    entry.Delete();
                }
            }
        }

        Directory.Delete(set, true);
        Log.StaleSetPruned(_logger, set);
    }

    /// <summary>True when <paramref name="path" /> is a real directory (not a link) strictly under the cache root.</summary>
    private bool IsInsideRoot(string path) =>
        new DirectoryInfo(path).LinkTarget is null
        && Path.GetFullPath(path).StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static FileStream? TryLock(string setDirectory, FileShare share)
    {
        try
        {
            return new FileStream(Path.Combine(setDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, share);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void DeleteIfPresent(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 439, Level = LogLevel.Information,
            Message = "Another process is compiling the Neural Engine models in {Directory}; waiting for it to finish, then loading its result.")]
        public static partial void WaitingForOtherProcess(ILogger logger, string directory);

        [LoggerMessage(EventId = 440, Level = LogLevel.Information,
            Message = "Deleted a Neural Engine model cache for another model or ONNX Runtime version: {Directory}.")]
        public static partial void StaleSetPruned(ILogger logger, string directory);

        [LoggerMessage(EventId = 441, Level = LogLevel.Warning,
            Message = "Could not {Operation} {Path}: {Reason}. Embedding is unaffected; doctor may show a stale state or cache size.")]
        public static partial void WriteFailed(ILogger logger, string operation, string path, string reason);
    }
}

/// <summary>A held lock on one cache set; disposing it releases the lock.</summary>
internal sealed class CoreMlCacheLease : IDisposable
{
    private readonly CoreMlCache _cache;
    private readonly Lock _gate = new();
    private FileStream? _lock;
    private bool _disposed;

    internal CoreMlCacheLease(CoreMlCache cache, FileStream @lock, bool compiling)
    {
        _cache = cache;
        _lock = @lock;
        Compiling = compiling;
    }

    /// <summary>True when this process holds the set exclusively to compile it; false when it loads a finished set.</summary>
    public bool Compiling { get; private set; }

    /// <summary>Records the set as finished and keeps serving from it under a shared lock.</summary>
    public void MarkComplete()
    {
        lock (_gate)
        {
            if (!Compiling || _disposed)
            {
                return;
            }

            _cache.WriteMarker();
            _lock?.Dispose();
            _lock = _cache.LockShared();
            Compiling = false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _lock?.Dispose();
        }
    }
}
