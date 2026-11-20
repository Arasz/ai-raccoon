using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiRaccoon.Infrastructure.Chunking;

/// <summary>Reads a JSON string value, with each unpaired surrogate escape (<c>\uD800</c>–<c>\uDFFF</c>) read as U+FFFD.</summary>
internal static class JsonStringReader
{
    private const string Replacement = "\\uFFFD";

    /// <summary>The element's string value; an unpaired surrogate escape that <see cref="JsonElement.GetString" /> refuses reads as U+FFFD.</summary>
    public static string Read(JsonElement element)
    {
        try
        {
            return element.GetString() ?? "";
        }
        catch (InvalidOperationException)
        {
            return JsonSerializer.Deserialize<string>(ReplaceLoneSurrogates(element.GetRawText())) ?? "";
        }
    }

    /// <summary>Rewrites each unpaired surrogate escape in a raw JSON string literal as <c>�</c>; a valid pair and other escapes stay as written.</summary>
    internal static string ReplaceLoneSurrogates(string literal)
    {
        var builder = new StringBuilder(literal.Length);
        var i = 0;
        while (i < literal.Length)
        {
            if (literal[i] != '\\' || i + 1 >= literal.Length)
            {
                builder.Append(literal[i++]);
                continue;
            }

            if (Escaped(literal, i) is not { } unit || !char.IsSurrogate(unit))
            {
                builder.Append(literal, i, 2);
                i += 2;
                continue;
            }

            if (char.IsHighSurrogate(unit) && Escaped(literal, i + 6) is { } low && char.IsLowSurrogate(low))
            {
                builder.Append(literal, i, 12);
                i += 12;
                continue;
            }

            builder.Append(Replacement);
            i += 6;
        }

        return builder.ToString();
    }

    private static char? Escaped(string literal, int at) =>
        at + 6 <= literal.Length && literal[at] == '\\' && literal[at + 1] == 'u'
                                 && ushort.TryParse(literal.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
            ? (char)code
            : null;
}
