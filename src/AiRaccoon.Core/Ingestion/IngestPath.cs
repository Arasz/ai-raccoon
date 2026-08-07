using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Path identity for every disk-reading surface (D3): absolute via <see cref="Path.GetFullPath"/>, separators and
///     ".." resolved, trailing separator stripped except root; case comparison follows the
///     host OS (Windows ignores case, Unix is ordinal).
/// </summary>
public static class IngestPath
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Host-OS case comparison for ingest paths.</summary>
    public static StringComparer PathComparer { get; } =
        Comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        Guard.IsNotNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>True when the path equals the scope entry or lies under it (entry covers all subdirectories).</summary>
    public static bool IsWithinScope(string path, string scopeEntry)
    {
        var candidate = Normalize(path);
        var scope = Normalize(scopeEntry);
        if (PathComparer.Equals(candidate, scope))
        {
            return true;
        }

        var prefix = scope + Path.DirectorySeparatorChar;
        return candidate.Length > prefix.Length &&
               string.Compare(candidate, 0, prefix, 0, prefix.Length, Comparison) == 0;
    }
}
