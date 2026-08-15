using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     The residual ADR-0066 named, closed. `TestData.EnvVarGate` was built for the classes that
///     **write** the process-global `AIRACCOON_DB_PASSPHRASE`; a class that merely **reads** the
///     environment — which any test opening a bank through the real host does, because the DI graph
///     registers `EnvEncryptionKeyProvider` — could overlap a writer and open a plain bank with a key.
///     SQLite reports that as error 26, "file is not a database", and it was the cause of WP19's flake.
///     <para>
///         Nothing stopped the next such test from being written without the gate. This does.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EnvGateReaderRuleTests
{
    private const string HostFactory = "CreateServerHost";

    /// <summary>Any of these means the file takes the gate, as a reader or as a writer.</summary>
    private static readonly string[] TakesTheGate =
        ["HoldEnvGateAsync", "EnvVarGate", "EnvScope.AcquireAsync"];

    [Fact]
    public void EveryTestClassThatOpensABankThroughTheHost_TakesTheEnvGate()
    {
        var offenders = TestFiles()
            .Where(file => Contains(file, HostFactory) && IsTestClass(file) && !Contains(file, TakesTheGate))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty(
            "a class that stands up the real host reads AIRACCOON_DB_PASSPHRASE whether it means to or "
            + "not, so it must hold TestData.EnvVarGate for the duration (docs/adr/0066): "
            + string.Join("; ", offenders));
    }

    /// <summary>An empty sweep passes for the same reason a broken one does.</summary>
    [Fact]
    public void EveryTestClassThatOpensABankThroughTheHost_ScansANonEmptySet() =>
        TestFiles().Count(file => Contains(file, HostFactory) && IsTestClass(file))
            .ShouldBeGreaterThanOrEqualTo(8, "the rule scans this set; if it empties, it stops being able to fail");

    private static IEnumerable<string> TestFiles() =>
        Directory.EnumerateFiles(TestsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                           && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string TestsRoot =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(
            TestData.RepoFile("tests/AiRaccoon.Tests/Unit/Layering/EnvGateReaderRuleTests.cs"))))!;

    /// <summary>A shared harness that never runs a test of its own is not the thing that must gate.</summary>
    private static bool IsTestClass(string file) =>
        Contains(file, "[Fact]") || Contains(file, "[Theory]");

    private static bool Contains(string file, params string[] needles)
    {
        var text = File.ReadAllText(file);
        return needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
    }
}
