using System.Text;
using System.Text.RegularExpressions;

namespace AiRaccoon.Core.Memory.QueryGuard.Structural;

/// <summary>
///     30 shape statistics of a text — line layout, punctuation/character-class density, token
///     shape, prose markers — with no embedding and no domain vocabulary (docs/adr/0041). Ported
///     line-for-line from the research record's Python reference (`gen2/features.py`, 31 features)
///     with `compress_ratio` dropped: F5/F7 measured it contributes nothing once line-layout
///     features are present (0.963 held-out AUC with or without) and it was the only allocating
///     step, cutting p50 extraction cost from 0.141ms to 0.107ms. Pure, synchronous, no I/O.
/// </summary>
public static partial class StructuralNoiseFeatures
{
    public const int FeatureCount = 30;

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}|\d{2}:\d{2}:\d{2}[.,]?\d*|\d{4}/\d{2}/\d{2}")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8,}\b|\b[A-Za-z0-9+/]{24,}={0,2}\b")]
    private static partial Regex HexRunRegex();

    [GeneratedRegex(@"https?://|\bwww\.")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^[\s\-=_*#~+|.]{6,}$")]
    private static partial Regex SeparatorLineRegex();

    [GeneratedRegex(@"^\s+(at |File ""|from |in )")]
    private static partial Regex StackFrameLineRegex();

    [GeneratedRegex(@"^\s*[\w.\-\[\]]+\s*[:=]\s*\S")]
    private static partial Regex KeyValueLineRegex();

    [GeneratedRegex(@"[A-Za-z]+(?:'[A-Za-z]+)?|\S")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[/\\][\w.\-]+[/\\]|^[/~.]?[\w.\-]*[/\\][\w.\-/\\]+$")]
    private static partial Regex PathTokenRegex();

    private static readonly HashSet<string> Pronouns = new(StringComparer.Ordinal)
    {
        "i", "we", "you", "my", "our", "your", "me", "us", "i'm", "we're",
        "you're", "i've", "we've", "mine", "yours", "ours",
    };

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "to",
        "of", "and", "or", "but", "in", "on", "for", "with", "that",
        "this", "it", "as", "at", "by", "from", "not", "no", "so", "if",
        "then", "than", "because", "which", "what", "when", "how", "why",
        "do", "does", "did", "has", "have", "had", "can", "could",
        "should", "would", "will", "there", "their", "they", "its",
    };

    /// <summary>
    ///     Extracts the 30 shape statistics for <paramref name="text" />. Feature order:
    ///     log_chars, n_lines, mean_line_len, cv_line_len, max_line_len_norm,
    ///     frac_lines_indented, frac_lines_dup, frac_sep_lines, frac_kv_lines, frac_stack_frames,
    ///     punct_ratio, digit_ratio, upper_ratio, space_ratio, bracket_ratio, slash_ratio,
    ///     type_token_ratio, mean_token_len, frac_word_tokens, frac_dirty_tokens,
    ///     path_token_ratio, ansi_density, ts_density, hexrun_density, url_density,
    ///     pronoun_ratio, stopword_ratio, sentence_end_ratio, char_entropy, mean_words_per_line.
    /// </summary>
    public static double[] Extract(string text)
    {
        var f = new double[FeatureCount];
        var n = text.Length;
        if (n == 0)
        {
            return f;
        }

        var lines = text.Split('\n');
        var nl = lines.Length;

        // Line/text lengths are counted in Unicode codepoints, not UTF-16 code units, to match
        // Python's len() (the reference implementation, gen2/features.py) exactly — a lone
        // astral-plane character (e.g. an emoji seen in real proc_notif output) is one Python
        // character but two C# chars.
        var lineLens = new int[nl];
        double sumLen = 0, maxLen = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int indented = 0, dupCount = 0, sep = 0, kv = 0, sf = 0;
        for (var li = 0; li < nl; li++)
        {
            var line = lines[li];
            var len = CodepointCount(line);
            lineLens[li] = len;
            sumLen += len;
            if (len > maxLen)
            {
                maxLen = len;
            }

            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                indented++;
            }

            if (!seen.Add(line))
            {
                dupCount++;
            }

            if (SeparatorLineRegex().IsMatch(line))
            {
                sep++;
            }

            if (KeyValueLineRegex().IsMatch(line))
            {
                kv++;
            }

            if (StackFrameLineRegex().IsMatch(line))
            {
                sf++;
            }
        }

        var meanLen = sumLen / nl;
        double varSum = 0;
        foreach (var len in lineLens)
        {
            var d = len - meanLen;
            varSum += d * d;
        }

        var cv = meanLen > 0 ? Math.Sqrt(varSum / nl) / meanLen : 0;

        int codepointN = 0, punct = 0, digits = 0, upper = 0, space = 0, brack = 0, slash = 0;
        var charCounts = new Dictionary<int, int>();
        foreach (var rune in text.EnumerateRunes())
        {
            codepointN++;
            if (Rune.IsWhiteSpace(rune))
            {
                space++;
            }
            else if (Rune.IsDigit(rune))
            {
                digits++;
            }
            else if (!Rune.IsLetterOrDigit(rune))
            {
                punct++;
                if (rune.Value is '{' or '}' or '[' or ']' or '(' or ')' or '<' or '>')
                {
                    brack++;
                }
                else if (rune.Value is '/' or '\\')
                {
                    slash++;
                }
            }

            if (Rune.IsUpper(rune))
            {
                upper++;
            }

            charCounts[rune.Value] = charCounts.TryGetValue(rune.Value, out var c) ? c + 1 : 1;
        }

        // .NET regex matches \S per UTF-16 code unit, so a lone astral-plane character (e.g. an
        // emoji seen in real proc_notif output) yields two adjacent 1-char matches — Python's re
        // (codepoint-based) yields one. MergeSurrogatePairMatches folds those back into a single
        // logical token to keep nt/uniqueTokens/etc. in parity with the reference implementation.
        var tokens = MergeSurrogatePairMatches(text, TokenRegex().Matches(text));
        var nt = Math.Max(tokens.Count, 1);
        var uniqueTokens = new HashSet<string>(StringComparer.Ordinal);
        double tokenLenSum = 0;
        int wordTokens = 0, dirtyTokens = 0, pronounTokens = 0, stopwordTokens = 0;
        foreach (var token in tokens)
        {
            tokenLenSum += CodepointCount(token);
            var lower = token.ToLowerInvariant();
            uniqueTokens.Add(lower);

            var allAlpha = true;
            foreach (var ch in token)
            {
                if (!char.IsLetter(ch))
                {
                    allAlpha = false;
                    break;
                }
            }

            var asciiLowerWord = true;
            foreach (var ch in lower)
            {
                if (ch is < 'a' or > 'z')
                {
                    asciiLowerWord = false;
                    break;
                }
            }

            if (asciiLowerWord)
            {
                wordTokens++;
            }

            if (!allAlpha)
            {
                dirtyTokens++;
            }

            if (Pronouns.Contains(lower))
            {
                pronounTokens++;
            }

            if (Stopwords.Contains(lower))
            {
                stopwordTokens++;
            }
        }

        var whitespaceTokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var pathTokens = 0;
        foreach (var w in whitespaceTokens)
        {
            if (PathTokenRegex().IsMatch(w))
            {
                pathTokens++;
            }
        }

        var ansiDensity = (double)AnsiRegex().Count(text) / nl;
        var tsDensity = (double)TimestampRegex().Count(text) / nl;
        var hexDensity = (double)HexRunRegex().Count(text) / nl;
        var urlDensity = (double)UrlRegex().Count(text) / nl;

        double sentenceEnds = 0;
        for (var i = 0; i + 1 < n; i++)
        {
            if (text[i] is '.' or '?' or '!' && text[i + 1] is ' ' or '\n')
            {
                sentenceEnds++;
            }
        }

        double entropy = 0;
        foreach (var count in charCounts.Values)
        {
            var p = (double)count / codepointN;
            entropy -= p * Math.Log2(p);
        }

        f[0] = Math.Log(1 + codepointN);
        f[1] = Math.Log(1 + nl);
        f[2] = meanLen;
        f[3] = cv;
        f[4] = Math.Log(1 + maxLen);
        f[5] = (double)indented / nl;
        f[6] = (double)dupCount / nl;
        f[7] = (double)sep / nl;
        f[8] = (double)kv / nl;
        f[9] = (double)sf / nl;
        f[10] = (double)punct / codepointN;
        f[11] = (double)digits / codepointN;
        f[12] = (double)upper / codepointN;
        f[13] = (double)space / codepointN;
        f[14] = (double)brack / codepointN;
        f[15] = (double)slash / codepointN;
        f[16] = (double)uniqueTokens.Count / nt;
        f[17] = tokenLenSum / nt;
        f[18] = (double)wordTokens / nt;
        f[19] = (double)dirtyTokens / nt;
        f[20] = (double)pathTokens / Math.Max(whitespaceTokens.Length, 1);
        f[21] = ansiDensity;
        f[22] = tsDensity;
        f[23] = hexDensity;
        f[24] = urlDensity;
        f[25] = (double)pronounTokens / nt;
        f[26] = (double)stopwordTokens / nt;
        f[27] = sentenceEnds / nt;
        f[28] = entropy;
        f[29] = (double)nt / nl;
        return f;
    }

    /// <summary>
    ///     Folds adjacent 1-char matches that together form a surrogate pair into one logical
    ///     token, so token statistics match Python re's codepoint-based matching. See the
    ///     provenance note at the call site.
    /// </summary>
    private static List<string> MergeSurrogatePairMatches(string text, MatchCollection matches)
    {
        var tokens = new List<string>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (m.Length == 1 && char.IsHighSurrogate(m.Value[0]) && i + 1 < matches.Count)
            {
                var next = matches[i + 1];
                if (next.Length == 1 && char.IsLowSurrogate(next.Value[0]) && next.Index == m.Index + m.Length)
                {
                    tokens.Add(text.Substring(m.Index, 2));
                    i++;
                    continue;
                }
            }

            tokens.Add(m.Value);
        }

        return tokens;
    }

    /// <summary>Unicode codepoint count of <paramref name="span" /> — matches Python's len(), unlike .NET's UTF-16 String.Length.</summary>
    private static int CodepointCount(ReadOnlySpan<char> span)
    {
        var count = 0;
        var i = 0;
        while (i < span.Length)
        {
            i += char.IsHighSurrogate(span[i]) && i + 1 < span.Length && char.IsLowSurrogate(span[i + 1]) ? 2 : 1;
            count++;
        }

        return count;
    }
}
