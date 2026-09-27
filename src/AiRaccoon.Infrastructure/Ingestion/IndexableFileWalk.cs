using System.IO.Enumeration;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>The directory walk shared by the watch catch-up scan and directory ingest.</summary>
public sealed class IndexableFileWalk()
{
    /// <summary>
    ///     Lazily lists every file under <paramref name="root" />, never descending into a hidden or
    ///     <see cref="WatchDenySet" /> directory. Callers still apply their per-file exclusion checks.
    /// </summary>
    public IEnumerable<string> Under(string root) =>
        new FileSystemEnumerable<string>(root, (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false })
        {
            ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
                !entry.FileName.StartsWith('.') && !WatchDenySet.Names.Contains(entry.FileName.ToString())
        };
}
