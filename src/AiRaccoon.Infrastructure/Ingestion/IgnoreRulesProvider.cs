using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Reads and parses `&lt;root&gt;/ai-raccoon.ignore` (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.1/§5.2): one file per watch/ingest root, never nested discovery. The rules a caller sees
///     are the ones on disk whenever the file changed; an unchanged file is answered from the last
///     parse (ADR-0151).
/// </summary>
public interface IIgnoreRulesProvider
{
    Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Drops the cached rules for <paramref name="root" />, so the next load reads the file again.
    ///     Called when the watch observes an edit to the ignore file itself: that event is positive
    ///     evidence the rules may have moved, which a stamp comparison cannot always see (a coarse
    ///     filesystem, or a rewrite that keeps both the write time and the length).
    /// </summary>
    void Invalidate(string root);
}

/// <summary>
///     Reads one `&lt;root&gt;/ai-raccoon.ignore`, resolved to <see cref="IgnoreRules.Empty" /> when the
///     file is absent. The parsed rules are cached per root and reused while the file's last write
///     time and length stay put (ADR-0151): a watch digest asks on every filesystem event, so a
///     read-and-parse per event was the cost. A moved file is re-read, so a caller still sees the
///     rules that are on disk whenever they changed.
/// </summary>
public sealed class IgnoreRulesProvider : IIgnoreRulesProvider
{
    public const string FileName = "ai-raccoon.ignore";

    /// <summary>
    ///     Whether a leaf name is the ignore file itself — case follows the host OS, as every other
    ///     path comparison here does (a Windows watch reports the name in the file's own casing).
    /// </summary>
    public static bool IsIgnoreFileName(string fileName) =>
        string.Equals(fileName, FileName, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);

    private readonly Dictionary<string, (IgnoreRules Rules, FileStamp Stamp)> _cache = new(IngestPath.PathComparer);
    private readonly Dictionary<string, long> _generationByRoot = new(IngestPath.PathComparer);
    private readonly Lock _gate = new();

    public async Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        // One spelling per root: "…/repo" and "…/repo/" are the same rules file, and an invalidation
        // must reach the entry whichever form the caller used.
        var key = IngestPath.Normalize(root);
        var path = Path.Combine(key, FileName);
        if (StampOf(path) is not { } stamp)
        {
            // A cached entry must not outlive the file: a delete followed by a restore that lands
            // the same stamp would otherwise serve the rules from before the gap.
            Invalidate(key);
            return IgnoreRules.Empty;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
            {
                return cached.Rules;
            }
        }

        // Read the generation before the file: an invalidation of THIS root that lands while the read
        // is in flight must not be overwritten by rules parsed before it.
        var generation = GenerationOf(key);
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Invalidate(key);
            return IgnoreRules.Empty; // deleted between the stat and the read
        }

        var rules = IgnoreRules.Parse(content);
        lock (_gate)
        {
            if (_generationByRoot.GetValueOrDefault(key) == generation)
            {
                _cache[key] = (rules, stamp);
            }
        }

        return rules;
    }

    public void Invalidate(string root)
    {
        var key = IngestPath.Normalize(root);
        lock (_gate)
        {
            _generationByRoot[key] = _generationByRoot.GetValueOrDefault(key) + 1;
            _cache.Remove(key);
        }
    }

    private long GenerationOf(string key)
    {
        lock (_gate)
        {
            return _generationByRoot.GetValueOrDefault(key);
        }
    }

    /// <summary>What a cache entry is keyed on — an edit moves the write time, the length, or both.</summary>
    private readonly record struct FileStamp(DateTime LastWriteUtc, long Length);

    private static FileStamp? StampOf(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new FileStamp(file.LastWriteTimeUtc, file.Length) : null;
    }
}

/// <summary>
///     Null object for callers that construct their dependency without DI (test/legacy call
///     sites keeping a positional constructor working) — resolves to no rules, ever.
/// </summary>
public sealed class NullIgnoreRulesProvider : IIgnoreRulesProvider
{
    public static NullIgnoreRulesProvider Instance { get; } = new();

    public void Invalidate(string root)
    {
    }

    public Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default) => Task.FromResult(IgnoreRules.Empty);
}
