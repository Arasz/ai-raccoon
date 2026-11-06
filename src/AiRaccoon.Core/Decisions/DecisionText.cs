using System.Globalization;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Decisions;

/// <summary>Bounds untrusted decision content before persistence; lengths are measured on trimmed text.</summary>
public static class DecisionText
{
    public static void Required(string value, int maximum, string parameter)
    {
        Guard.IsNotNullOrWhiteSpace(value, parameter);
        Guard.HasSizeLessThanOrEqualTo(value.Trim(), maximum, parameter);
    }

    /// <summary>Refuses padding and characters that render invisibly or break lines.</summary>
    public static void Identifier(string value, string parameter)
    {
        Required(value, 200, parameter);
        Guard.IsTrue(value.Length == value.Trim().Length, parameter);
        Guard.IsFalse(value.Any(IsHidden), parameter);
    }

    private static bool IsHidden(char character) =>
        char.IsControl(character)
        || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
