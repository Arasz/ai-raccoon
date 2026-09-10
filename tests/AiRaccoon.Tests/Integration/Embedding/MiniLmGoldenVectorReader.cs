using System.Text.Json;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     Reads the (id, query) pairs of the eval-set-100 corpus for the golden-vector capture.
///     Accepts both committed shapes: the pre-header bare array and {header, queries}.
/// </summary>
internal static class MiniLmGoldenVectorReader
{
    public static IReadOnlyList<(string Id, string Query)> ReadEvalSetQueries(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var entries = root.ValueKind == JsonValueKind.Object ? root.GetProperty("queries") : root;
        return entries.EnumerateArray()
            .Select(item => (item.GetProperty("id").GetString()!, item.GetProperty("query").GetString()!))
            .ToList();
    }
}
