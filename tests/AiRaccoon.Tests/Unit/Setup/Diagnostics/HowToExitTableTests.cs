using System.Text.RegularExpressions;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Diagnostics;

/// <summary>
///     Derive gate for the doctor exit-code table (docs/how-to/configure-ai-raccoon-server.md,
///     "composes into a script" section): the backticked code set must equal the doctor-reachable
///     <see cref="ExitCode" /> consts enumerated by name (R1 F6), and each Meaning cell must carry
///     the pinned phrase — table-own for 0/1/2 (no doc comments), cross-checked against the const's
///     doc comment in ExitCode.cs for 19/20/22/24 (R1 F1/F6, R2 F4/F8).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class HowToExitTableTests
{
    private const string HowToPath = "docs/how-to/configure-ai-raccoon-server.md";
    private const string ExitCodePath = "src/AiRaccoon/ExitCode.cs";

    private static readonly Dictionary<int, (string ConstName, string Phrase, bool DocCommentCrossCheck)> ExpectedRows =
        new()
        {
            [ExitCode.Success] = ("Success", "HEALTHY", false),
            [ExitCode.FailedToResolveEncryptionKey] = ("FailedToResolveEncryptionKey", "the encryption key could not be resolved", false),
            [ExitCode.FailedToOpenEncryptedBank] = ("FailedToOpenEncryptedBank", "the bank could not be opened read-only", false),
            [ExitCode.SchemaVerificationFailed] = ("SchemaVerificationFailed", "the bank's actual schema", true),
            [ExitCode.SchemaNewerThanBinary] = ("SchemaNewerThanBinary", "user_version", true),
            [ExitCode.NoBank] = ("NoBank", "no bank file exists at the resolved path", true),
            [ExitCode.ModelMigrationOpen] = ("ModelMigrationOpen", "every MCP tool call is refused until", true)
        };

    [Fact]
    public void DoctorTable_ListsExactlyTheDoctorReachableExitCodes()
    {
        DoctorTable().Keys.OrderBy(k => k).ShouldBe(ExpectedRows.Keys.OrderBy(k => k));
    }

    [Fact]
    public void DoctorTable_EachMeaning_CarriesThePinnedPhrase()
    {
        var table = DoctorTable();
        var exitCodeSource = File.ReadAllText(TestData.RepoFile(ExitCodePath));
        foreach (var (code, (constName, phrase, crossCheck)) in ExpectedRows)
        {
            table[code].ShouldContain(phrase, Case.Sensitive,
                $"row {code} ({constName}) must keep its pinned phrase in the Meaning cell");
            if (crossCheck)
            {
                DocCommentFor(exitCodeSource, constName).ShouldContain(phrase, Case.Sensitive,
                    $"the {constName} doc comment must keep the same phrase the table pins");
            }
        }
    }

    private static Dictionary<int, string> DoctorTable()
    {
        var howTo = File.ReadAllText(TestData.RepoFile(HowToPath));
        var anchor = howTo.IndexOf("composes into a script:", StringComparison.Ordinal);
        anchor.ShouldBeGreaterThan(0, "the doctor exit table must follow the 'composes into a script' sentence");
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
