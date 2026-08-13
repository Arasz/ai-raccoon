using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Chunking;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>
///     Token-bounded chunker for JSON files. Preserves key structure for objects/arrays,
///     with automatic fallback to line-based chunking for malformed JSON.
/// </summary>
public sealed class JsonFileTypeChunker : IJsonChunker
{
    private readonly TokenCount _countTokens;
    private readonly IMarkdownChunker _fallbackChunker;
    private readonly int _overlayTokens;

    public JsonFileTypeChunker(TokenCount countTokens, IMarkdownChunker fallbackChunker, int overlayTokens)
    {
        Guard.IsNotNull(countTokens);
        Guard.IsNotNull(fallbackChunker);
        Guard.IsGreaterThanOrEqualTo(overlayTokens, 0);
        _countTokens = countTokens;
        _fallbackChunker = fallbackChunker;
        _overlayTokens = overlayTokens;
    }

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
                JsonValueKind.Object => ChunkObject(root, maxTokens, text),
                JsonValueKind.Array => ChunkArray(root, maxTokens, text),
                _ => ChunkFallback(text, maxTokens)
            };
        }
        catch (JsonException)
        {
            return ChunkFallback(text, maxTokens);
        }
    }

    private IReadOnlyList<string> ChunkObject(JsonElement root, int maxTokens, string rawText)
    {
        // Structural grouping is deliberately non-overlapping: chunks are key/item-bounded, so
        // markdown-style overlap would duplicate whole properties. The overlay budget (ctor
        // config) reaches only the line-based fallback — oversized single properties, empty result.
        var rawTokens = _countTokens(rawText);
        if (rawTokens <= maxTokens)
        {
            return [rawText.Trim()];
        }

        List<string> chunks = [];
        var currentProps = new List<JsonProperty>();
        var currentTokens = _countTokens("{\n}");

        foreach (var prop in root.EnumerateObject())
        {
            var propText = $"  \"{prop.Name}\": {prop.Value.GetRawText()}";
            var propTokens = _countTokens(propText);

            if (currentProps.Count > 0 && currentTokens + propTokens + 1 > maxTokens)
            {
                chunks.Add(BuildObjectChunk(currentProps));
                currentProps.Clear();
                currentTokens = _countTokens("{\n}");
            }

            if (propTokens + currentTokens > maxTokens)
            {
                var propChunk = $"{{\n{propText}\n}}";
                var subChunks = ChunkFallback(propChunk, maxTokens);
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

        return chunks.Count > 0 ? chunks : ChunkFallback(rawText, maxTokens);
    }

    private IReadOnlyList<string> ChunkArray(JsonElement root, int maxTokens, string rawText)
    {
        // Same non-overlapping contract as ChunkObject: items are whole, so overlap would
        // duplicate them; the overlay budget reaches only the fallback (see above).
        var rawTokens = _countTokens(rawText);
        if (rawTokens <= maxTokens)
        {
            return [rawText.Trim()];
        }

        List<string> chunks = [];
        var currentItems = new List<string>();
        var currentTokens = _countTokens("[\n]");

        foreach (var item in root.EnumerateArray())
        {
            var itemText = item.GetRawText();
            var itemTokens = _countTokens(itemText);

            if (currentItems.Count > 0 && currentTokens + itemTokens + 1 > maxTokens)
            {
                chunks.Add("[\n  " + string.Join(",\n  ", currentItems) + "\n]");
                currentItems.Clear();
                currentTokens = _countTokens("[\n]");
            }

            if (itemTokens + currentTokens > maxTokens)
            {
                var subChunks = ChunkFallback(itemText, maxTokens);
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

        return chunks.Count > 0 ? chunks : ChunkFallback(rawText, maxTokens);
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

    private IReadOnlyList<string> ChunkFallback(string text, int maxTokens) =>
        _fallbackChunker.Chunk(text, maxTokens, Math.Min(_overlayTokens, Math.Max(0, maxTokens - 1)));
}
