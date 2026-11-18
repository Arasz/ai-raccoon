using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Watch;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Primitives;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Reads and parses `&lt;root&gt;/ai-raccoon.ignore` (docs/work/2026-08-21-code-search-implementation-plan.md
///     §2.1/§5.2): one file per watch/ingest root, never nested discovery. An unchanged file is
///     answered from the last parse; a change shows by the next load, at most one polling interval
///     (4 s) late unless the root's digest invalidates it first.
/// </summary>
public interface IIgnoreRulesProvider
{
    Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Drops the cached rules for <paramref name="root" /> so the next load reads the file again. Called when
    ///     the watch observes an edit to the ignore file: a polling token sees a change up to 4 s late, this at once.
    /// </summary>
    void Invalidate(string root);
}

/// <summary>
///     Reads one `&lt;root&gt;/ai-raccoon.ignore`, resolved to <see cref="IgnoreRules.Empty" /> when the
///     file is absent. Rules are cached per root behind a change token taken before the read, so a
///     change during a read is never lost. Dispose stops the per-root file providers.
/// </summary>
public sealed class IgnoreRulesProvider(Func<string, IFileProvider> fileProviderFactory) : IIgnoreRulesProvider, IDisposable
{
    public const string FileName = "ai-raccoon.ignore";

    /// <summary>How a leaf name is compared to <see cref="FileName" />: ignoring case on Windows and macOS, whose volumes are case-insensitive by default.</summary>
    internal static StringComparison NameComparison { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly Dictionary<string, RootState> _roots = new(IngestPath.PathComparer);
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Whether a leaf name is the ignore file itself, compared as the host OS compares names.</summary>
    public static bool IsIgnoreFileName(string fileName) => IsIgnoreFileName(fileName, NameComparison);

    internal static bool IsIgnoreFileName(string fileName, StringComparison comparison) => string.Equals(fileName, FileName, comparison);

    /// <summary>
    ///     A file provider for <paramref name="root" /> that polls the file's write time lazily, with no
    ///     <see cref="FileSystemWatcher" />. Neither the root nor the file has to exist.
    /// </summary>
    public static IFileProvider CreatePhysicalProvider(string root) =>
        new PhysicalFileProvider(root, ExclusionFilters.None) { UsePollingFileWatcher = true, UseActivePolling = true };

    public async Task<IgnoreRules> LoadAsync(string root, CancellationToken cancellationToken = default)
    {
        // One spelling per root: "…/repo" and "…/repo/" are the same rules file, and an invalidation
        // must reach the entry whichever form the caller used.
        var key = IngestPath.Normalize(root);
        IChangeToken token;
        IFileProvider provider;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = StateFor(key);
            // Taken before the file is read: a change that lands during the read fires this token.
            token = new CompositeChangeToken([state.Provider.Watch(FileName), new CancellationChangeToken(state.Invalidated.Token)]);
            if (state.Entry is { } cached && !cached.Token.HasChanged)
            {
                return cached.Rules;
            }

            provider = state.Provider;
        }

        var rules = await ReadAsync(provider, cancellationToken);
        lock (_gate)
        {
            if (_roots.TryGetValue(key, out var state))
            {
                state.Entry = rules is null ? null : new CacheEntry(rules, token);
            }
        }

        return rules ?? IgnoreRules.Empty;
    }

    public void Invalidate(string root)
    {
        var key = IngestPath.Normalize(root);
        lock (_gate)
        {
            if (!_roots.TryGetValue(key, out var state))
            {
                return;
            }

            var old = state.Invalidated;
            state.Invalidated = new CancellationTokenSource();
            state.Entry = null;
            old.Cancel();
            old.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var state in _roots.Values)
            {
                state.Invalidated.Cancel();
                state.Invalidated.Dispose();
                (state.Provider as IDisposable)?.Dispose();
            }

            _roots.Clear();
        }
    }

    private static async Task<IgnoreRules?> ReadAsync(IFileProvider provider, CancellationToken cancellationToken)
    {
        var file = provider.GetFileInfo(FileName);
        if (!file.Exists)
        {
            return null;
        }

        try
        {
            await using var stream = file.CreateReadStream();
            using var reader = new StreamReader(stream);
            return IgnoreRules.Parse(await reader.ReadToEndAsync(cancellationToken));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // deleted between the stat and the read
        }
    }

    private RootState StateFor(string key)
    {
        if (!_roots.TryGetValue(key, out var state))
        {
            state = new RootState(fileProviderFactory(key));
            _roots[key] = state;
        }

        return state;
    }

    private sealed record CacheEntry(IgnoreRules Rules, IChangeToken Token);

    private sealed class RootState(IFileProvider provider)
    {
        public IFileProvider Provider { get; } = provider;

        public CancellationTokenSource Invalidated { get; set; } = new();

        public CacheEntry? Entry { get; set; }
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
