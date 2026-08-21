using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiRaccoon.Infrastructure.Embedding.Manifest;

/// <summary>The manifest file is not valid JSON, or its shape does not match the pinned v1 schema.</summary>
public sealed class EmbeddingManifestFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
///     JSON (de)serializer for the pinned v1 manifest schema: camelCase field names, kebab-case
///     enum values (the schema spells <c>model-output</c>, not <c>modelOutput</c>), explicit
///     nulls for the nullable fields. Unknown enum values fail deserialization with an actionable
///     message naming the field path; required fields are enforced by <c>[JsonRequired]</c>.
/// </summary>
public static class EmbeddingManifestSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // The manifest is a data file, never embedded in HTML — keep special-token names like
        // "<s>" literal instead of \u003C escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new KebabCaseEnumJsonConverter<TokenizerFamily>(),
            new KebabCaseEnumJsonConverter<PoolingMode>(),
            new KebabCaseEnumJsonConverter<NormalizationMode>(),
            new KebabCaseEnumJsonConverter<ManifestProvider>()
        }
    };

    public static string Serialize(EmbeddingManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    public static EmbeddingManifest Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<EmbeddingManifest>(json, Options)
                   ?? throw new EmbeddingManifestFormatException("invalid embedding manifest: the document is empty");
        }
        catch (JsonException ex)
        {
            var at = string.IsNullOrEmpty(ex.Path) ? string.Empty : $" (at {ex.Path})";
            throw new EmbeddingManifestFormatException($"invalid embedding manifest: {ex.Message}{at}", ex);
        }
    }
}

/// <summary>Pins the schema's exact string for an enum member where hump-to-kebab would guess
/// wrong — e.g. <c>SentencePiece</c> is <c>sentencepiece</c> in the schema, not
/// <c>sentence-piece</c>.</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class SchemaNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
///     Serializes enum members as their schema name (kebab-case; overridable per member) and
///     reads them back case-insensitively; an unsupported value fails with a message naming the
///     field path and the supported values.
/// </summary>
public sealed class KebabCaseEnumJsonConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (raw is not null)
        {
            foreach (var member in Enum.GetValues<TEnum>())
            {
                if (string.Equals(SchemaName(member), raw, StringComparison.OrdinalIgnoreCase))
                {
                    return member;
                }
            }
        }

        throw new JsonException(
            $"'{raw}' is not a supported {typeof(TEnum).Name} value; supported values: {string.Join(", ", Supported())}");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(SchemaName(value));

    private static IEnumerable<string> Supported() => Enum.GetValues<TEnum>().Select(SchemaName);

    private static string SchemaName(TEnum value)
    {
        var member = Enum.GetName(value)!;
        var attribute = typeof(TEnum).GetField(member)?.GetCustomAttribute<SchemaNameAttribute>();
        return attribute?.Name ?? HumpToKebab(member);
    }

    private static string HumpToKebab(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var ch in name)
        {
            if (char.IsUpper(ch) && builder.Length > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
