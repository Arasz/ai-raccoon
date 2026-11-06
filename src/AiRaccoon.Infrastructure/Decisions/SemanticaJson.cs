using System.Text;
using System.Text.Json;

namespace AiRaccoon.Infrastructure.Decisions;

/// <summary>Strict JSON reading shared by the Semantica parsers: strict UTF-8, no duplicate keys, safe string reads.</summary>
internal static class SemanticaJson
{
    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    public static JsonDocument Parse(byte[] bytes, string parameter)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ArgumentException($"The {parameter} file is not valid UTF-8.", parameter, exception);
        }

        return ParseText(text, parameter);
    }

    public static JsonDocument ParseText(string text, string parameter)
    {
        try
        {
            return JsonDocument.Parse(text, Strict);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"The {parameter} file is not valid JSON without duplicate keys: {exception.Message}", parameter, exception);
        }
    }

    /// <summary>The string value, or null when the element is not a string or holds text that cannot be decoded.</summary>
    public static string? ReadString(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return element.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Refuses unless the element is an object whose property names are exactly <paramref name="names" />.</summary>
    public static void RequireExactKeys(JsonElement element, string where, string parameter, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException($"{where} must be a JSON object.", parameter);
        }

        var found = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!found.SetEquals(names))
        {
            throw new ArgumentException($"{where} must have exactly the keys {string.Join(", ", names)}; found {string.Join(", ", found.Order(StringComparer.Ordinal))}.", parameter);
        }
    }
}
