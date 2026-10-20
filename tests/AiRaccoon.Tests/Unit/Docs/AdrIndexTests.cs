using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Docs;

/// <summary>
///     docs/adr/README.md is a hand-written index of docs/adr/*.md. A hand-maintained mirror
///     drifts the moment someone adds to one side only, and nothing notices — so it is derived
///     and compared here rather than trusted (.ai-badger/invariants/derive-or-delete-the-list.md).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class AdrIndexTests
{
    [Fact]
    public void Index_ListsEveryAdrOnDisk()
    {
        var (onDisk, indexed) = Read();

        var missing = onDisk.Keys.Except(indexed).Order().ToList();

        missing.ShouldBeEmpty(
            $"docs/adr/README.md must link every ADR; missing: {string.Join(", ", missing.Select(n => onDisk[n]))}");
    }

    [Fact]
    public void Index_LinksNoAdrThatIsGone()
    {
        var (onDisk, indexed) = Read();

        var stale = indexed.Except(onDisk.Keys).Order().ToList();

        stale.ShouldBeEmpty($"docs/adr/README.md links ADRs that no longer exist: {string.Join(", ", stale)}");
    }

    /// <summary>
    ///     An unrecorded gap means a number was claimed and the file never landed — two ADRs can
    ///     then be written against the same next number in different branches. A deliberate skip is
    ///     told apart from a lost file by the index's "Numbers never used" table, nothing else.
    /// </summary>
    [Fact]
    public void AdrNumbers_HaveNoUnrecordedGaps()
    {
        var (onDisk, _) = Read();
        var numbers = onDisk.Keys.Order().ToList();

        numbers.ShouldNotBeEmpty();
        var gaps = Enumerable.Range(numbers[0], numbers[^1] - numbers[0] + 1)
            .Except(numbers)
            .Except(RecordedSkips())
            .ToList();

        gaps.ShouldBeEmpty(
            $"docs/adr has unrecorded gaps at {string.Join(", ", gaps.Select(n => n.ToString("D4")))}; either the file is missing or the skip belongs in the index's \"Numbers never used\" table");
    }

    /// <summary>A recorded skip that a document later filled is a stale note, not a licence.</summary>
    [Fact]
    public void RecordedSkips_NameNoAdrThatExists()
    {
        var (onDisk, _) = Read();

        var contradicted = RecordedSkips().Intersect(onDisk.Keys).Order().ToList();

        contradicted.ShouldBeEmpty(
            $"the index records {string.Join(", ", contradicted.Select(n => n.ToString("D4")))} as never used, but those ADRs exist");
    }

    /// <summary>
    ///     The index records supersession; the ADR's own Status line is what a reader who opens the
    ///     file sees. When they disagree, the file wins the reader and loses the truth — ADR-0029 and
    ///     ADR-0030 each read `Accepted` for a day while describing code that had been deleted.
    /// </summary>
    [Fact]
    public void SupersededAdrs_DoNotStillCallThemselvesAccepted()
    {
        var (onDisk, _) = Read();
        var directory = Path.GetDirectoryName(TestData.RepoFile("docs/adr/README.md"))!;

        var lying = SupersededByIndex()
            .Where(onDisk.ContainsKey)
            .Where(number => Regex.IsMatch(
                File.ReadAllText(Path.Combine(directory, onDisk[number])),
                @"^(##\s*Status\s*\r?\n|Status:\s*)Accepted\s*$",
                RegexOptions.Multiline))
            .Order()
            .ToList();

        lying.ShouldBeEmpty(
            $"the index records {string.Join(", ", lying.Select(n => n.ToString("D4")))} as superseded or reversed, but the ADR's own Status line still reads Accepted — a reader who opens the file directly sees a live decision. Follow ADR-0002's pattern and say so in the Status line");
    }

    /// <summary>
    ///     An `## Amendment …` / `## Amendments …` section, or a line that opens with
    ///     `Amended &lt;date&gt;`. Those are the two forms this repo writes, so the column is
    ///     mechanically derivable. A supersession or a reversal is recorded in the summary cell
    ///     and mirrored in the record's Status line, and
    ///     <see cref="SupersededAdrs_DoNotStillCallThemselvesAccepted" /> is what guards that
    ///     pair.
    ///     </para>
    /// </summary>
    [Fact]
    public void AmendedColumn_IsDerivedFromTheRecordItSummarises()
    {
        var (header, rows) = ReadRows();

        header.ShouldContain("Amended",
            "the ADR index needs an `Amended` column: a frozen ADR can only be amended in the index");

        var wrongWidth = rows.Where(row => row.Cells.Count != header.Count).ToList();
        wrongWidth.ShouldBeEmpty(
            $"ADR index rows must match the header's {header.Count} columns; wrong: {string.Join(", ", wrongWidth.Select(r => r.Number.ToString("D4")))}");

        var directory = Path.GetDirectoryName(TestData.RepoFile("docs/adr/README.md"))!;
        var column = header.IndexOf("Amended");
        var problems = new List<string>();

        foreach (var row in rows)
        {
            var file = row.Cells[0].Split('(').Last().TrimEnd(')');
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                continue;
            }

            var dates = AmendmentDates(File.ReadAllText(path));
            var cell = row.Cells[column];

            if (dates.Count == 0)
            {
                if (cell != "—")
                {
                    problems.Add($"{row.Number:D4}: the cell says '{cell}' but the record shows no amendment");
                }

                continue;
            }

            if (cell == "—")
            {
                problems.Add($"{row.Number:D4}: the record shows an amendment ({string.Join(", ", dates)}) and the cell is empty");
                continue;
            }

            var missing = dates.Where(date => !cell.Contains(date, StringComparison.Ordinal)).ToList();
            if (missing.Count > 0)
            {
                problems.Add($"{row.Number:D4}: the cell omits {string.Join(", ", missing)}");
            }

            var invented = cell.Split(',').Select(part => part.Trim())
                .Where(part => part.Length > 0 && part != "—")
                .Where(date => !dates.Contains(date))
                .ToList();
            if (invented.Count > 0)
            {
                problems.Add($"{row.Number:D4}: the cell names {string.Join(", ", invented)}, which the record does not show");
            }
        }

        problems.ShouldBeEmpty($"the `Amended` column drifted from the records:\n  {string.Join("\n  ", problems)}");
    }

    /// <summary>
    ///     A record may file its amendments under a plural heading, one entry per sub-heading.
    ///     The dates on the sub-headings are the amendment dates; a date mentioned in the
    ///     section's prose is not, and neither is the record's own acceptance date.
    /// </summary>
    [Fact]
    public void AmendmentDates_ReadsAnAmendmentsSectionAndItsSubheadings()
    {
        const string text = """
            # 0001 — Example
            Date: 2026-01-01
            Status: Accepted
            ## Amendments

            ### 2026-02-03 — the first change

            prose that mentions 2026-04-05 in passing

            ### 2026-06-07 — the second change
            """;

        AmendmentDates(text).ShouldBe(["2026-02-03", "2026-06-07"],
            "the sub-headings of an `## Amendments` section are the amendment entries; a date in the prose is not");
    }

    /// <summary>An `## Amendment …` / `## Amendments …` section heading (the whole line, so a date on it counts), or a line that opens with `Amended &lt;date&gt;`.</summary>
    private static readonly Regex AmendmentHeading = new(@"^##\s+Amendments?\b.*$", RegexOptions.Multiline);

    /// <summary>A sub-heading inside an amendment section — one entry per amendment.</summary>
    private static readonly Regex AmendmentSubHeading = new(@"^#{3,6}\s+.*$", RegexOptions.Multiline);

    /// <summary>The next `## ` heading — the end of an amendment section.</summary>
    private static readonly Regex SectionEnd = new(@"^## ", RegexOptions.Multiline);

    private static readonly Regex AmendmentLine = new(@"^\s*Amended (\d{4}-\d{2}-\d{2})\b", RegexOptions.Multiline);

    private static readonly Regex DateToken = new(@"\b\d{4}-\d{2}-\d{2}\b");

    /// <summary>
    ///     The dates the record's own amendment markers carry: the heading of an
    ///     `## Amendment(s)` section and the sub-headings inside it (one entry per amendment),
    ///     plus every `Amended &lt;date&gt;` line. The section's prose is not read — a date
    ///     mentioned in passing is not an amendment.
    /// </summary>
    private static List<string> AmendmentDates(string text)
    {
        var dates = new List<string>();

        void Add(string date)
        {
            if (!dates.Contains(date))
            {
                dates.Add(date);
            }
        }

        foreach (Match heading in AmendmentHeading.Matches(text))
        {
            foreach (Match date in DateToken.Matches(heading.Value))
            {
                Add(date.Value);
            }

            var body = text[(heading.Index + heading.Length)..];
            var end = SectionEnd.Match(body);
            var section = end.Success ? body[..end.Index] : body;
            foreach (Match sub in AmendmentSubHeading.Matches(section))
            {
                foreach (Match date in DateToken.Matches(sub.Value))
                {
                    Add(date.Value);
                }
            }
        }

        foreach (Match line in AmendmentLine.Matches(text))
        {
            Add(line.Groups[1].Value);
        }

        return dates;
    }

    private sealed record IndexRow(int Number, List<string> Cells);

    private static (List<string> Header, List<IndexRow> Rows) ReadRows()
    {
        var header = new List<string>();
        var rows = new List<IndexRow>();

        foreach (var line in File.ReadAllLines(TestData.RepoFile("docs/adr/README.md")))
        {
            if (line.StartsWith("| ADR ", StringComparison.Ordinal))
            {
                header = Cells(line);
                continue;
            }

            if (!line.StartsWith("| [", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = Cells(line);
            var link = Regex.Match(cells[0], @"\((?<file>\d{4}-[^)]*\.md)\)");
            rows.Add(new IndexRow(int.Parse(link.Groups["file"].Value[..4]), cells));
        }

        return (header, rows);
    }

    /// <summary>Splits a table row on unescaped pipes only (`\|` is content, not a column break).</summary>
    private static List<string> Cells(string line) =>
        [.. Regex.Split(line.Trim().Trim('|'), @"(?<!\\)\|").Select(cell => cell.Trim())];

    /// <summary>ADRs whose own index row says they were superseded or reversed *by* something else.</summary>
    private static HashSet<int> SupersededByIndex() =>
    [
        .. Regex.Matches(File.ReadAllText(TestData.RepoFile("docs/adr/README.md")),
                @"^\| *\[(\d{4})[^|]*\|(?<body>[^|]*)\|", RegexOptions.Multiline)
            .Where(m => Regex.IsMatch(m.Groups["body"].Value, @"(superseded|reversed)\b[^.;|]*\bby\b",
                RegexOptions.IgnoreCase))
            .Select(m => int.Parse(m.Groups[1].Value))
    ];

    private static HashSet<int> RecordedSkips()
    {
        var section = Regex.Match(File.ReadAllText(TestData.RepoFile("docs/adr/README.md")),
            @"^## Numbers never used\s*$(.*)", RegexOptions.Singleline | RegexOptions.Multiline);
        return section.Success
            ?
            [
                .. Regex.Matches(section.Groups[1].Value, @"^\| *(\d{4}) *\|", RegexOptions.Multiline)
                    .Select(m => int.Parse(m.Groups[1].Value))
            ]
            : [];
    }

    private static (Dictionary<int, string> OnDisk, HashSet<int> Indexed) Read()
    {
        var readme = TestData.RepoFile("docs/adr/README.md");
        var directory = Path.GetDirectoryName(readme)!;

        var onDisk = Directory.EnumerateFiles(directory, "*.md")
            .Select(Path.GetFileName)
            .Where(name => Regex.IsMatch(name!, @"^\d{4}-"))
            .ToDictionary(name => int.Parse(name![..4]), name => name!);

        var indexed = Regex.Matches(File.ReadAllText(readme), @"\((\d{4})-[^)]*\.md\)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();

        return (onDisk, indexed);
    }
}
