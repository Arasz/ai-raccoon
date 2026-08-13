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
    public static string ResolveReal(string path)
    {
        var normalized = Normalize(path);
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        var segments = normalized[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        var current = root.Length == 0 ? normalized : Path.TrimEndingDirectorySeparator(root);
        foreach (var segment in segments)
        {
            current = ResolveSegment(Path.Combine(current, segment));
        }

        return current;
    }

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
