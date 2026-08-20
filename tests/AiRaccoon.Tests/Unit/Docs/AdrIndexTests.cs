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

        gaps.ShouldBeEmpty($"docs/adr has unrecorded gaps at {string.Join(", ", gaps.Select(n => n.ToString("D4")))}; " +
                           "either the file is missing or the skip belongs in the index's \"Numbers never used\" table");
    }

    /// <summary>A recorded skip that a document later filled is a stale note, not a licence.</summary>
    [Fact]
    public void RecordedSkips_NameNoAdrThatExists()
    {
        var (onDisk, _) = Read();

        var contradicted = RecordedSkips().Intersect(onDisk.Keys).Order().ToList();

        contradicted.ShouldBeEmpty(
            $"the index records {string.Join(", ", contradicted.Select(n => n.ToString("D4")))} as never used, " +
            "but those ADRs exist");
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
            $"the index records {string.Join(", ", lying.Select(n => n.ToString("D4")))} as superseded or " +
            "reversed, but the ADR's own Status line still reads Accepted — a reader who opens the file " +
            "directly sees a live decision. Follow ADR-0002's pattern and say so in the Status line");
    }

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
