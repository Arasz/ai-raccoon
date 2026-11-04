using System.Globalization;
using System.Text;

namespace AiRaccoon.Decisions.Client;

/// <summary>Makes decision text safe for a terminal or a Markdown reader without changing its length in Unicode scalars.</summary>
internal static class TerminalText
{
    /// <summary>Replaces every control (except a line feed), format and separator rune with U+FFFD, one rune for one.</summary>
    public static string Sanitize(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            result.Append(IsUnsafe(rune) && rune.Value != '\n' ? Rune.ReplacementChar.ToString() : rune.ToString());
        }

        return result.ToString();
    }

    /// <summary>Writes every control, format and separator rune as a visible <c>\uXXXX</c> escape.</summary>
    public static string Visible(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            result.Append(IsUnsafe(rune) ? $"\\u{rune.Value:X4}" : rune.ToString());
        }

        return result.ToString();
    }

    private static bool IsUnsafe(Rune rune) =>
        Rune.IsControl(rune)
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
