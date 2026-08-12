using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Chunking;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
/// Token-bounded chunker for JSON files. Preserves key structure and schema context for objects/arrays,
/// with automatic fallback to line-based chunking for malformed JSON.
/// </summary>
public sealed class JsonFileTypeChunker : IChunker
{
    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Object => ChunkObject(root, maxTokens, overlayTokens, text),
                JsonValueKind.Array => ChunkArray(root, maxTokens, overlayTokens, text),
                _ => ChunkFallback(text, maxTokens, overlayTokens)
            };
        }
        catch (JsonException)
        {
            return ChunkFallback(text, maxTokens, overlayTokens);
        }
    }

    /// <summary>
    /// Extract a compact structural schema summary from a JSON text representation.
    /// </summary>
    public static string ExtractSchemaSummary(string jsonText)
    {
        if (string.IsNullOrWhiteSpace(jsonText))
        {
            return "{}";
        }

        try
        {
            var node = JsonNode.Parse(jsonText);
            return BuildNodeSchema(node);
        }
        catch (JsonException)
        {
            return "{}";
        }
    }

    private static string BuildNodeSchema(JsonNode? node)
    {
        if (node is null)
        {
            return "null";
        }

        if (node is JsonObject obj)
        {
            var props = obj.Select(p => $"\"{p.Key}\": {BuildNodeSchema(p.Value)}");
            return "{\n  " + string.Join(",\n  ", props) + "\n}";
        }

        if (node is JsonArray arr)
        {
            if (arr.Count == 0)
            {
                return "[]";
            }
            return "[" + BuildNodeSchema(arr[0]) + "]";
        }

        var kind = node.GetValueKind();
        return kind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant()
        };
    }

    private static IReadOnlyList<string> ChunkObject(JsonElement root, int maxTokens, int overlayTokens, string rawText)
    {
        var rawTokens = TokenizerChunker.CountTokens(rawText);
        if (rawTokens <= maxTokens)
        {
            return [rawText.Trim()];
        }

        List<string> chunks = [];
        var currentProps = new List<JsonProperty>();
        var currentTokens = TokenizerChunker.CountTokens("{\n}");

        foreach (var prop in root.EnumerateObject())
        {
            var propText = $"  \"{prop.Name}\": {prop.Value.GetRawText()}";
            var propTokens = TokenizerChunker.CountTokens(propText);

            if (currentProps.Count > 0 && currentTokens + propTokens + 1 > maxTokens)
            {
                chunks.Add(BuildObjectChunk(currentProps));
                currentProps.Clear();
                currentTokens = TokenizerChunker.CountTokens("{\n}");
            }

            if (propTokens + currentTokens > maxTokens)
            {
                var propChunk = $"{{\n{propText}\n}}";
                var subChunks = ChunkFallback(propChunk, maxTokens, overlayTokens);
                chunks.AddRange(subChunks);
            }
            else
            {
                currentProps.Add(prop);
                currentTokens += propTokens + 1;
            }
        }

        if (currentProps.Count > 0)
        {
            chunks.Add(BuildObjectChunk(currentProps));
        }

        return chunks.Count > 0 ? chunks : ChunkFallback(rawText, maxTokens, overlayTokens);
    }

    private static IReadOnlyList<string> ChunkArray(JsonElement root, int maxTokens, int overlayTokens, string rawText)
    {
        var rawTokens = TokenizerChunker.CountTokens(rawText);
        if (rawTokens <= maxTokens)
        {
            return [rawText.Trim()];
        }

        List<string> chunks = [];
        var currentItems = new List<string>();
        var currentTokens = TokenizerChunker.CountTokens("[\n]");

        foreach (var item in root.EnumerateArray())
        {
            var itemText = item.GetRawText();
            var itemTokens = TokenizerChunker.CountTokens(itemText);

            if (currentItems.Count > 0 && currentTokens + itemTokens + 1 > maxTokens)
            {
                chunks.Add("[\n  " + string.Join(",\n  ", currentItems) + "\n]");
                currentItems.Clear();
                currentTokens = TokenizerChunker.CountTokens("[\n]");
            }

            if (itemTokens + currentTokens > maxTokens)
            {
                var subChunks = ChunkFallback(itemText, maxTokens, overlayTokens);
                chunks.AddRange(subChunks);
            }
            else
            {
                currentItems.Add(itemText);
                currentTokens += itemTokens + 1;
            }
        }

        if (currentItems.Count > 0)
        {
            chunks.Add("[\n  " + string.Join(",\n  ", currentItems) + "\n]");
        }

        return chunks.Count > 0 ? chunks : ChunkFallback(rawText, maxTokens, overlayTokens);
    }

    private static string BuildObjectChunk(List<JsonProperty> props)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        for (var i = 0; i < props.Count; i++)
        {
            var p = props[i];
            sb.Append("  \"").Append(p.Name).Append("\": ").Append(p.Value.GetRawText());
            if (i < props.Count - 1)
            {
                sb.Append(',');
            }
            sb.AppendLine();
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static IReadOnlyList<string> ChunkFallback(string text, int maxTokens, int overlayTokens) =>
        MarkdownChunker.Split(text, maxTokens, overlayTokens, TokenizerChunker.CountTokens);
}
