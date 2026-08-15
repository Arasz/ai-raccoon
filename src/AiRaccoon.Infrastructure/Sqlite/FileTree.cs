using System.Text.Json;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     `memory_list`'s nested-directory JSON. A pure function over paths — it never read the bank,
///     which is why it belongs beside <see cref="ContextFilter" /> rather than inside the store.
/// </summary>
internal static class FileTree
{
    /// <summary>Nests each '/'-separated segment; siblings are ordered ordinally, so the shape does not move with the host locale.</summary>
    public static string Build(IEnumerable<string> paths)
    {
        var root = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var node = root;
            var segments = path.Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (!node.TryGetValue(segments[i], out var child) || child is not SortedDictionary<string, object> dir)
                {
                    dir = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    node[segments[i]] = dir;
                }

                node = dir;
            }

            node[segments[^1]] = new object();
        }

        return JsonSerializer.Serialize(root);
    }
}
