using System.Globalization;
using System.Text;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Projects;

/// <summary>
///     Renders caller-supplied ids and names safe to echo: control, format, surrogate and
///     private-use code points become <c>\uXXXX</c> escapes and the result is capped, so a
///     refusal cannot carry raw terminal control into stderr or a response body.
/// </summary>
public static class ProjectIdText
{
    /// <summary>The default cap, large enough for a full 200-character id or name.</summary>
    public const int DefaultMaxLength = 200;

    /// <summary>
    ///     <paramref name="value" /> with every control, format, surrogate and private-use code
    ///     point escaped, truncated to at most <paramref name="maxLength" /> characters; truncation
    ///     never splits an escape.
    /// </summary>
    public static string Printable(string value, int maxLength = DefaultMaxLength)
    {
        Guard.IsNotNull(value);
        Guard.IsGreaterThanOrEqualTo(maxLength, 0);

        var builder = new StringBuilder();
        for (var i = 0; i < value.Length && builder.Length < maxLength; i++)
        {
            var ch = value[i];
            string token;
            if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var pair = new Rune(ch, value[i + 1]);
                token = NeedsEscape(Rune.GetUnicodeCategory(pair))
                    ? $"\\u{(int)ch:X4}\\u{(int)value[i + 1]:X4}"
                    : new string([ch, value[i + 1]]);
                i++;
            }
            else
            {
                token = char.IsSurrogate(ch) || NeedsEscape(CharUnicodeInfo.GetUnicodeCategory(ch))
                    ? $"\\u{(int)ch:X4}"
                    : ch.ToString();
            }

            if (builder.Length + token.Length > maxLength)
            {
                break;
            }

            builder.Append(token);
        }

        return builder.ToString();
    }

    private static bool NeedsEscape(UnicodeCategory category) =>
        category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate
            or UnicodeCategory.PrivateUse;
}
