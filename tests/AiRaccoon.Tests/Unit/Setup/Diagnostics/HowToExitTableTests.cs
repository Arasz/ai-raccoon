using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Diagnostics;

/// <summary>
///     Derive gate for the exit tables in docs/how-to/configure-ai-raccoon-server.md: every row is
///     '| `NN` | `Category.Name` — meaning |', the named <see cref="ErrorCode" /> constant must hold
///     exactly NN (read by reflection, so a renumbering cannot leave the docs behind), and each table
///     lists exactly the codes its command family can exit with.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed partial class HowToExitTableTests
{
    private const string HowToPath = "docs/how-to/configure-ai-raccoon-server.md";

    /// <summary>What `doctor` can exit with.</summary>
    private static readonly int[] DoctorCodes =
    [
        ErrorCode.Ok.Success,
        ErrorCode.Key.Unresolved, ErrorCode.Key.BwsNotInstalled, ErrorCode.Key.BwsTimedOut, ErrorCode.Key.BwsFailed,
        ErrorCode.Key.SecretNotAKey, ErrorCode.Key.SourceSidecarInvalid,
        ErrorCode.Bank.OpenFailed, ErrorCode.Bank.NoBank, ErrorCode.Bank.Corrupted, ErrorCode.Bank.Busy,
        ErrorCode.Bank.SchemaMismatch, ErrorCode.Bank.SchemaNewerThanBinary, ErrorCode.Bank.MigrationOpen
    ];

    /// <summary>What a settings verb can exit with once its value parsed: the settings-server channel's own failures.</summary>
    private static readonly int[] SettingsCodes =
    [
        ErrorCode.Usage.UndialablePort, ErrorCode.Usage.RequestRejected, ErrorCode.Bank.NoBank,
        ErrorCode.Server.NoToken, ErrorCode.Server.RequestTokenRefused, ErrorCode.Server.EndpointMissing, ErrorCode.Server.MigrationRefused,
        ErrorCode.Reach.Unavailable, ErrorCode.Reach.StoppedAnswering, ErrorCode.Reach.PrivateFallbackFailed,
        ErrorCode.Reach.StartFailed, ErrorCode.Reach.AutoStartUnsupported,
        ErrorCode.Internal.ServerError, ErrorCode.Internal.UnusableResponse, ErrorCode.Ok.SIGC
    ];

    [Theory]
    [InlineData("composes into a script:")]
    [InlineData("never reports success:")]
    public void EveryRow_NamesTheConstantThatHoldsItsCode(string anchor)
    {
        var constants = Constants();
        foreach (var (code, name) in Table(anchor))
        {
            constants.ShouldContainKey(name, $"row {code} names '{name}', which is not an ErrorCode constant");
            constants[name].ShouldBe(code, $"row {code} names {name}, which is {constants[name]}");
        }
    }

    [Fact]
    public void DoctorTable_ListsExactlyTheCodesDoctorExitsWith()
    {
        Table("composes into a script:").Keys.ShouldBe(DoctorCodes, ignoreOrder: true);
    }

    [Fact]
    public void SettingsTable_ListsExactlyTheSettingsChannelCodes()
    {
        Table("never reports success:").Keys.ShouldBe(SettingsCodes, ignoreOrder: true);
    }

    private static Dictionary<string, int> Constants() =>
        typeof(ErrorCode).GetNestedTypes(BindingFlags.Public)
            .SelectMany(category => category.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .Select(field => (Name: $"{category.Name}.{field.Name}", Value: (int)field.GetRawConstantValue()!)))
            .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    private static Dictionary<int, string> Table(string anchor)
    {
        var howTo = File.ReadAllText(TestData.RepoFile(HowToPath));
        var start = howTo.IndexOf(anchor, StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0, $"the exit table must follow the '{anchor}' sentence");
        var header = howTo.IndexOf("| Exit code | Meaning |", start, StringComparison.Ordinal);
        header.ShouldBeGreaterThan(0, "an '| Exit code | Meaning |' table must exist after the anchor");
        var rowsStart = howTo.IndexOf('\n', howTo.IndexOf('\n', header) + 1) + 1;
        var end = howTo.IndexOf("\n\n", rowsStart, StringComparison.Ordinal);
        var rows = new Dictionary<int, string>();
        foreach (var line in howTo[rowsStart..(end < 0 ? howTo.Length : end)].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Row().Match(line);
            match.Success.ShouldBeTrue($"every exit-table row must be '| `NN` | `Category.Name` — meaning |' (line: {line})");
            rows.Add(int.Parse(match.Groups["code"].Value, CultureInfo.InvariantCulture), match.Groups["name"].Value);
        }

        return rows;
    }

    [GeneratedRegex(@"^\|\s*`(?<code>\d+)`\s*\|\s*`(?<name>\w+\.\w+)`\s+—.*\|$")]
    private static partial Regex Row();
}
