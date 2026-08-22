namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Directory names skipped during directory-watch/ingest enumeration by default — dependency
///     trees and VCS internals (docs/work/2026-08-21-code-search-implementation-plan.md §2.3,
///     owner-approved v1 default, OQ8). `ai-raccoon.ignore` is the user-facing extension surface
///     for anything not on this list.
/// </summary>
public static class WatchDenySet
{
    public static IReadOnlySet<string> Names { get; } = new HashSet<string>(IngestPath.PathComparer)
    {
        "node_modules", "bin", "obj", ".git", ".venv", "__pycache__", "dist", "build", "target"
    };

    /// <summary>
    ///     The one skip rule for a directory watch or ingest (ADR-0086 §6), shared by enumeration
    ///     and by the digest of a filesystem event: true when a segment strictly below
    ///     <paramref name="root" /> is hidden (starts with `.`) or is in <see cref="Names" />.
    ///     A root that is itself the target — a watch on a single file — is never excluded.
    /// </summary>
    public static bool Excludes(string root, string path)
    {
        var normalizedRoot = IngestPath.Normalize(root);
        var normalizedPath = IngestPath.Normalize(path);
        return !IngestPath.PathComparer.Equals(normalizedRoot, normalizedPath) &&
               IngestPath.HasHiddenOrDeniedSegment(normalizedRoot, normalizedPath, Names);
    }
}
