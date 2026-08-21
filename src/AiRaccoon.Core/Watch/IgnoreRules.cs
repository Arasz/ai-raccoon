namespace AiRaccoon.Core.Watch;

/// <summary>
///     Parsed `ai-raccoon.ignore` rules: gitignore subset — `*` (one segment), `**` (zero or more
///     segments, only as a whole segment), trailing `/` directory patterns, leading/anchored `/`,
///     no `!` negation (v1), any-match-wins, case comparison per host OS. Pure — no I/O; the file
///     read lives with the infrastructure provider that hands callers a parsed instance.
/// </summary>
public sealed class IgnoreRules : IEquatable<IgnoreRules>
{
    private static readonly StringComparison SegmentComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Pattern lines use `/` (gitignore convention); candidate paths handed in by callers
    /// are host-native (<see cref="Path.DirectorySeparatorChar" />) — both split the same way.</summary>
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>The exact text this instance was parsed from — value equality is defined over it
    /// (not the parsed pattern list, which has no natural equality) so callers can cheaply detect
    /// a changed ignore file between two loads (e.g. WatchCatchUp's mid-scan re-check).</summary>
    private readonly string _source;

    private readonly IReadOnlyList<IgnorePattern> _patterns;

    private IgnoreRules(string source, IReadOnlyList<IgnorePattern> patterns)
    {
        _source = source;
        _patterns = patterns;
    }

    /// <summary>No rules — nothing is ever ignored (missing ignore file resolves here).</summary>
    public static IgnoreRules Empty { get; } = new(string.Empty, []);

    public bool HasRules => _patterns.Count > 0;

    /// <summary>One pattern per line; blank lines and `#` comments are inert; `\r\n`/`\r` normalized first.</summary>
    public static IgnoreRules Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        List<IgnorePattern>? patterns = null;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            (patterns ??= []).Add(IgnorePattern.Parse(line));
        }

        return patterns is null ? Empty : new IgnoreRules(content, patterns);
    }

    /// <summary>
    ///     True when <paramref name="relativePath" /> (root-relative, `/`-separated, no leading
    ///     slash) is ignored by any pattern — a directory pattern also ignores every path beneath
    ///     it (ancestor match), regardless of which pattern in the file matched (any-match-wins).
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        if (_patterns.Count == 0)
        {
            return false;
        }

        var segments = relativePath.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in _patterns)
        {
            if (pattern.Matches(segments, isDirectory))
            {
                return true;
            }
        }

        return false;
    }

    public bool Equals(IgnoreRules? other) => other is not null && string.Equals(_source, other._source, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as IgnoreRules);

    public override int GetHashCode() => _source.GetHashCode(StringComparison.Ordinal);

    public static bool operator ==(IgnoreRules? left, IgnoreRules? right) => Equals(left, right);

    public static bool operator !=(IgnoreRules? left, IgnoreRules? right) => !Equals(left, right);

    /// <summary>One parsed pattern line.</summary>
    private sealed class IgnorePattern
    {
        private readonly bool _anchored;
        private readonly bool _directoryOnly;
        private readonly string[] _segments;

        private IgnorePattern(string[] segments, bool anchored, bool directoryOnly)
        {
            _segments = segments;
            _anchored = anchored;
            _directoryOnly = directoryOnly;
        }

        public static IgnorePattern Parse(string line)
        {
            var directoryOnly = line.EndsWith('/');
            var body = directoryOnly ? line[..^1] : line;
            // A slash anywhere (leading or interior) anchors the pattern to the root; only a
            // pattern with no slash at all matches at any depth (gitignore convention).
            var anchored = body.Contains('/', StringComparison.Ordinal);
            var trimmed = body.StartsWith('/') ? body[1..] : body;
            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return new IgnorePattern(segments.Length == 0 ? [""] : segments, anchored, directoryOnly);
        }

        public bool Matches(string[] pathSegments, bool isDirectory)
        {
            if (_anchored)
            {
                return MatchesAt(0, pathSegments, isDirectory);
            }

            // Unanchored (no slash in the source line) is always a single segment — try it at
            // every depth, exactly the gitignore "matches at any level below the root" rule.
            for (var start = 0; start < pathSegments.Length; start++)
            {
                if (MatchesAt(start, pathSegments, isDirectory))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     Matches the pattern starting at path position <paramref name="start" />. A
        ///     directory-only pattern ignores the matched span itself (only when it lands on a
        ///     directory node) and everything strictly beneath it; a plain pattern must consume
        ///     the path exactly (unless it ends in a trailing `**`, which reaches everything beneath).
        /// </summary>
        private bool MatchesAt(int start, string[] pathSegments, bool isDirectory)
        {
            for (var end = start; end <= pathSegments.Length; end++)
            {
                if (!Consumes(_segments, 0, pathSegments, start, end))
                {
                    continue;
                }

                if (end == pathSegments.Length)
                {
                    // Full consumption: a directory-only pattern only ignores the leaf itself when
                    // the leaf really is a directory; short of the leaf, it is always an ancestor.
                    if (!_directoryOnly || isDirectory)
                    {
                        return true;
                    }
                }
                else if (_directoryOnly)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>True when the pattern segments (from <paramref name="pi" />) exactly consume
        /// path segments [<paramref name="from" />, <paramref name="to" />), honoring `**` as
        /// zero-or-more segments.</summary>
        private static bool Consumes(string[] pattern, int pi, string[] path, int from, int to)
        {
            while (true)
            {
                if (pi == pattern.Length)
                {
                    return from == to;
                }

                var segment = pattern[pi];
                if (segment == "**")
                {
                    for (var skip = from; skip <= to; skip++)
                    {
                        if (Consumes(pattern, pi + 1, path, skip, to))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                if (from >= to || !SegmentMatches(segment, path[from]))
                {
                    return false;
                }

                pi++;
                from++;
            }
        }

        /// <summary>Classic two-pointer wildcard match: `*` is the only special character,
        /// standing for any run of characters (possibly empty) within this one segment.</summary>
        private static bool SegmentMatches(string globSegment, string pathSegment)
        {
            int p = 0, t = 0, star = -1, match = 0;
            while (t < pathSegment.Length)
            {
                if (p < globSegment.Length && globSegment[p] == '*')
                {
                    star = p++;
                    match = t;
                }
                else if (p < globSegment.Length && CharEquals(globSegment[p], pathSegment[t]))
                {
                    p++;
                    t++;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++match;
                }
                else
                {
                    return false;
                }
            }

            while (p < globSegment.Length && globSegment[p] == '*')
            {
                p++;
            }

            return p == globSegment.Length;
        }

        private static bool CharEquals(char a, char b) =>
            SegmentComparison == StringComparison.OrdinalIgnoreCase
                ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b)
                : a == b;
    }
}
