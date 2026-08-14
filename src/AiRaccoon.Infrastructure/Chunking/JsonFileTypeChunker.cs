using System.Text;
using System.Text.Json;
using AiRaccoon.Core.Chunking;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>
///     Token-bounded chunker for JSON files. Preserves key structure for objects/arrays,
///     with automatic fallback to line-based chunking for malformed JSON. No emitted chunk can
///     exceed maxTokens (docs/adr/0036): every candidate chunk is verified against the real
///     tokenizer before it is returned, and anything still over budget is handed to the
///     line/token-bounded markdown fallback, which always terminates.
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

    public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0, TokenCount? countTokens = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var count = countTokens ?? _countTokens;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Object => ChunkObject(root, maxTokens, text, count),
                JsonValueKind.Array => ChunkArray(root, maxTokens, text, count),
                _ => ChunkFallback(text, maxTokens, count)
            };
        }
        catch (JsonException)
        {
            return ChunkFallback(text, maxTokens, count);
        }
    }

    private IReadOnlyList<string> ChunkObject(JsonElement root, int maxTokens, string rawText, TokenCount countTokens)
    {
        // Structural grouping is deliberately non-overlapping: chunks are key/item-bounded, so
        // markdown-style overlap would duplicate whole properties. The overlay budget (ctor
        // config) reaches only the line-based fallback — oversized single properties, empty result.
        var rawTokens = countTokens(rawText);
        if (rawTokens <= maxTokens)
        {
            return [rawText.Trim()];
        }

        List<string> chunks = [];
        var currentProps = new List<JsonProperty>();
        var currentTokens = countTokens("{\n}");

        foreach (var prop in root.EnumerateObject())
        {
            var propText = $"  \"{prop.Name}\": {prop.Value.GetRawText()}";
            var propTokens = countTokens(propText);

            if (currentProps.Count > 0 && currentTokens + propTokens + 1 > maxTokens)
            {
                chunks.Add(BuildObjectChunk(currentProps));
                currentProps.Clear();
                currentTokens = countTokens("{\n}");
            }

            if (propTokens + currentTokens > maxTokens)
            {
                var propChunk = $"{{\n{propText}\n}}";
                var subChunks = ChunkFallback(propChunk, maxTokens, countTokens);
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

        return EnsureWithinBudget(chunks.Count > 0 ? chunks : ChunkFallback(rawText, maxTokens, countTokens),
            maxTokens, countTokens);
    }

    private IReadOnlyList<string> ChunkArray(JsonElement root, int maxTokens, string rawText, TokenCount countTokens)
    {
        // Same non-overlapping contract as ChunkObject: items are whole, so overlap would
        // duplicate them; the overlay budget reaches only the fallback (see above).
        var rawTokens = countTokens(rawText);
        if (rawTokens <= maxTokens)
        {
            return [rawText.Trim()];
        }

        List<string> chunks = [];
        var currentItems = new List<string>();
        var currentTokens = countTokens("[\n]");

        foreach (var item in root.EnumerateArray())
        {
            var itemText = item.GetRawText();
            var itemTokens = countTokens(itemText);

            if (currentItems.Count > 0 && currentTokens + itemTokens + 1 > maxTokens)
            {
                chunks.Add("[\n  " + string.Join(",\n  ", currentItems) + "\n]");
                currentItems.Clear();
                currentTokens = countTokens("[\n]");
            }

            if (itemTokens + currentTokens > maxTokens)
            {
                var subChunks = ChunkFallback(itemText, maxTokens, countTokens);
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

        return EnsureWithinBudget(chunks.Count > 0 ? chunks : ChunkFallback(rawText, maxTokens, countTokens),
            maxTokens, countTokens);
    }

    /// <summary>
    ///     Last line of defense (docs/adr/0036): the grouping loops above bound chunks by a summed
    ///     token estimate, which is not exact under a non-composable tokenizer. Any chunk that the
    ///     real tokenizer still measures over budget is handed to the token-bounded markdown
    ///     fallback instead of being returned as-is.
    /// </summary>
    private IReadOnlyList<string> EnsureWithinBudget(IReadOnlyList<string> chunks, int maxTokens, TokenCount countTokens)
    {
        List<string> result = [];
        foreach (var chunk in chunks)
        {
            if (countTokens(chunk) <= maxTokens)
            {
                result.Add(chunk);
            }
            else
            {
                result.AddRange(ChunkFallback(chunk, maxTokens, countTokens));
            }
        }

        return result;
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

    private IReadOnlyList<string> ChunkFallback(string text, int maxTokens, TokenCount countTokens) =>
        _fallbackChunker.Chunk(text, maxTokens, Math.Min(_overlayTokens, Math.Max(0, maxTokens - 1)), countTokens);
}
