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
}
