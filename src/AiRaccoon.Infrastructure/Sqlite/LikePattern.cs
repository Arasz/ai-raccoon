namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Escapes LIKE wildcards in a path so a prefix match stays literal (path cascade deletes).</summary>
internal static class LikePattern
{
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
