namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Bounds of a path's subtree as a half-open range, <c>path &gt;= Low AND path &lt; High</c>, for
///     path cascade deletes: an index can seek it, where a LIKE prefix scans. '0' is the character
///     after '/', so the range holds every "path/…" and nothing else.
/// </summary>
internal static class PathSubtree
{
    public static string Low(string path) => path + "/";

    public static string High(string path) => path + "0";
}
