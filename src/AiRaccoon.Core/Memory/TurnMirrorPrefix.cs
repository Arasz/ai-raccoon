using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory;

/// <summary>
///     Splits a candidate's value at an appended tool-call transcript instead of scoring the
///     whole thing as a turn-mirror (ported from agentC/scorer.py's mirror_prefix(), see
///     docs/adr/0018-promotion-scoring-v2.md v3 section).
/// </summary>
internal static partial class TurnMirrorPrefix
{
    private const int ProseRescueThreshold = 300;

    /// <summary>
    ///     The value to score (a prose prefix when a transcript starts late) and whether the
    ///     candidate is a true turn-mirror (transcript markup starts early and repeats).
    /// </summary>
    internal static (string Value, bool IsMirror) Split(string value)
    {
        var v = value ?? string.Empty;
        var matches = Markup().Matches(v);
        if (matches.Count == 0)
        {
            return (v, false);
        }

        var firstIndex = matches[0].Index;
        if (firstIndex >= ProseRescueThreshold)
        {
            return (v[..firstIndex], false);
        }

        return (v, matches.Count >= 2);
    }

    [GeneratedRegex(
        """</?(invoke|parameter|content|sourceFile|antml:invoke|function_calls)\b|<invoke\s+name=|<parameter\s+name=""",
        RegexOptions.IgnoreCase)]
    private static partial Regex Markup();
}
