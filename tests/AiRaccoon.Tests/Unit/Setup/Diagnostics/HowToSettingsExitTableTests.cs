using System.Text.RegularExpressions;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Diagnostics;

/// <summary>
///     Derive gate for the settings-command exit table (docs/how-to/configure-ai-raccoon-server.md,
///     "fails loudly" section — R1 F2): the backticked code set must equal the settings-command
///     <see cref="ExitCode" /> consts enumerated by name, and each Meaning cell's pinned phrase must
///     also occur in the const's doc comment in ExitCode.cs — the 25 row included, so the #592
///     refusal's docs cannot drift.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class HowToSettingsExitTableTests
{
    private const string HowToPath = "docs/how-to/configure-ai-raccoon-server.md";
    private const string ExitCodePath = "src/AiRaccoon/ExitCode.cs";

    private static readonly Dictionary<int, (string ConstName, string Phrase)> ExpectedRows =
        new()
        {
            [ExitCode.SettingsServerRefused] = ("SettingsServerRefused", "refused the loopback token"),
            [ExitCode.SettingsServerUnavailable] = ("SettingsServerUnavailable", "within the acquire budget"),
            [ExitCode.SettingsServerError] = ("SettingsServerError", "answered but failed"),
            [ExitCode.ModelResetRefused] = ("ModelResetRefused", "outbox row is open")
        };

    [Fact]
    public void SettingsTable_ListsExactlyTheSettingsCommandExitCodes()
    {
        SettingsTable().Keys.OrderBy(k => k).ShouldBe(ExpectedRows.Keys.OrderBy(k => k));
    }

    [Fact]
    public void SettingsTable_EachMeaning_CarriesThePinnedPhrase()
    {
        var table = SettingsTable();
        var exitCodeSource = File.ReadAllText(TestData.RepoFile(ExitCodePath));
        foreach (var (code, (constName, phrase)) in ExpectedRows)
        {
            table[code].ShouldContain(phrase, Case.Sensitive,
                $"row {code} ({constName}) must keep its pinned phrase in the Meaning cell");
            DocCommentFor(exitCodeSource, constName).ShouldContain(phrase, Case.Sensitive,
                $"the {constName} doc comment must keep the same phrase the table pins");
        }
    }

    private static Dictionary<int, string> SettingsTable()
    {
        var howTo = File.ReadAllText(TestData.RepoFile(HowToPath));
        var anchor = howTo.IndexOf("never reports success:", StringComparison.Ordinal);
        anchor.ShouldBeGreaterThan(0, "the settings exit table must follow the 'never reports success:' sentence");
        return ParseTable(howTo, anchor);
    }

    private static Dictionary<int, string> ParseTable(string text, int startIndex)
    {
        var header = text.IndexOf("| Exit code | Meaning |", startIndex, StringComparison.Ordinal);
        header.ShouldBeGreaterThan(0, "an '| Exit code | Meaning |' table must exist after the anchor");
        var rowsStart = text.IndexOf('\n', header) + 1;
        rowsStart = text.IndexOf('\n', rowsStart) + 1; // skip the |---|---| separator line
        var end = text.IndexOf("\n\n", rowsStart, StringComparison.Ordinal);
        if (end < 0)
        {
            end = text.Length;
        }

        var rows = new Dictionary<int, string>();
        foreach (var line in text[rowsStart..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\|\s*`(\d+)`\s*\|(.*)\|$");
            match.Success.ShouldBeTrue($"every exit-table row must be '| `N` | meaning |' (line: {line})");
            rows[int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)] =
                match.Groups[2].Value.Trim();
        }

        return rows;
    }

    private static string DocCommentFor(string exitCodeSource, string constName)
    {
        var declaration = $"public const int {constName} = ";
        var index = exitCodeSource.IndexOf(declaration, StringComparison.Ordinal);
        index.ShouldBeGreaterThan(0, $"{constName} must be declared in ExitCode.cs");
        var commentStart = exitCodeSource.LastIndexOf("/// <summary>", index, StringComparison.Ordinal);
        commentStart.ShouldBeGreaterThan(0, $"{constName} must carry a doc comment");
        var commentEnd = exitCodeSource.IndexOf("</summary>", commentStart, StringComparison.Ordinal);
        if (commentEnd < 0 || commentEnd > index)
        {
            commentEnd = index;
        }

        return exitCodeSource[commentStart..commentEnd];
    }
}
