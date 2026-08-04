using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Watch;

/// <summary>
///     Watch path identity (D3): absolute via <see cref="Path.GetFullPath"/>, separators and
///     ".." resolved, trailing separator stripped except root; case comparison follows the
///     host OS (Windows ignores case, Unix is ordinal).
/// </summary>
public static class WatchPath
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Host-OS case comparison for watch paths.</summary>
    public static StringComparer PathComparer { get; } =
        Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        Guard.IsNotNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>True when the watch path equals the scope entry or lies under it (entry covers all subdirectories).</summary>
    public static bool IsWithinScope(string watchPath, string scopeEntry)
    {
        var watch = Normalize(watchPath);
        var scope = Normalize(scopeEntry);
        if (PathComparer.Equals(watch, scope))
        {
            return true;
        }

        var prefix = scope + Path.DirectorySeparatorChar;
        return watch.Length > prefix.Length && string.Compare(watch, 0, prefix, 0, prefix.Length, Comparison) == 0;
    }
}
