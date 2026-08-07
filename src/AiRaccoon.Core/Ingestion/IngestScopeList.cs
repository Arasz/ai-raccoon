using System.Text.Json;

namespace AiRaccoon.Core.Ingestion;

/// <summary>
///     Serialization and mutation of an ingest scope allowlist (JSON array of absolute
///     paths): add normalizes with Path.GetFullPath semantics and dedups + re-sorts;
///     unparsable rows read as empty.
/// </summary>
public static class IngestScopeList
{
    public static IReadOnlyList<string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string ToJson(IEnumerable<string> paths) => JsonSerializer.Serialize(paths.ToList());

    public static IReadOnlyList<string> Add(IEnumerable<string> current, string path)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        return
        [
            .. current
                .Append(normalized)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    public static IReadOnlyList<string> Remove(IEnumerable<string> current, string path)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path);
        return
        [
            .. current
                .Where(p => !string.Equals(p, normalized, StringComparison.Ordinal))
        ];
    }
}
