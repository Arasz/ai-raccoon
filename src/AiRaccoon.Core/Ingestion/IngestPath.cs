using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Path identity for every disk-reading surface (docs/plans/file-watcher-implementation.md D3):
///     normalizes to absolute, resolved, host-OS-case form; <see cref="IsWithinScope" /> resolves
///     symlinks so containment can't be defeated by a link whose literal path reads as in-scope.
/// </summary>
public static class IngestPath
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Host-OS case comparison for ingest paths.</summary>
    public static StringComparer PathComparer { get; } =
        Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Textual normalization only — no symlink resolution. Used for path identity (dedup keys, event correlation) where I/O is unwanted and containment is not being decided.</summary>
    public static string Normalize(string path)
    {
        Guard.IsNotNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    ///     <see cref="Normalize" /> plus every symlink along the path resolved to its real target — a
    ///     symlinked ancestor or leaf both count. Falls back to the normalized segment when it does
    ///     not exist or cannot be inspected, so a not-yet-created path still normalizes instead of throwing.
    /// </summary>
    public static string ResolveReal(string path) => ResolveReal(path, 0);

    /// <summary>
    ///     A symlink's stored target can itself have a symlinked ancestor (e.g. macOS's
    ///     `/var` → `/private/var`) that <see cref="File.ResolveLinkTarget(string, bool)" />'s
    ///     "final target" does not walk — only re-resolving the substituted target from scratch
    ///     catches it. Depth-capped against a symlink cycle (ELOOP is a real OS error; this must
    ///     never hang).
    /// </summary>
    private static string ResolveReal(string path, int depth)
    {
        var normalized = Normalize(path);
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var segments = normalized[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root.Length == 0 ? normalized : Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            var resolved = ResolveSegment(candidate);
            current = depth < MaxSymlinkDepth && !PathComparer.Equals(resolved, candidate)
                ? ResolveReal(resolved, depth + 1)
                : resolved;
        }

        return current;
    }

    private const int MaxSymlinkDepth = 40;

    /// <summary>True when the path equals the scope entry or lies under it (entry covers all subdirectories), comparing real (symlink-resolved) paths on both sides.</summary>
    public static bool IsWithinScope(string path, string scopeEntry)
    {
        var candidate = ResolveReal(path);
        var scope = ResolveReal(scopeEntry);
        if (PathComparer.Equals(candidate, scope))
        {
            return true;
        }

        var prefix = scope + Path.DirectorySeparatorChar;
        return candidate.Length > prefix.Length &&
               string.Compare(candidate, 0, prefix, 0, prefix.Length, Comparison) == 0;
    }

    /// <summary>
    ///     True when any path segment strictly below <paramref name="root" /> (the leaf filename
    ///     included) starts with `.` or matches <paramref name="denySet" /> — enumeration skip for
    ///     directory watches/ingests. Segments at or above <paramref name="root" /> itself never
    ///     count, so a root that happens to live under a dot-prefixed ancestor (e.g. a worktree
    ///     checkout) does not disqualify everything beneath it.
    /// </summary>
    public static bool HasHiddenOrDeniedSegment(string root, string path, IReadOnlySet<string> denySet)
    {
        var relative = Path.GetRelativePath(Normalize(root), Normalize(path));
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('.') || denySet.Contains(segment))
            {
                return true;
            }
        }

        return false;
    }

    private static string ResolveSegment(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, true);
            return target is null ? path : Path.TrimEndingDirectorySeparator(target.FullName);
        }
        catch (IOException)
        {
            return path;
        }
        catch (UnauthorizedAccessException)
        {
            return path;
        }
    }
}
