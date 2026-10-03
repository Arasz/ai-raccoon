using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     The shared SQL helpers (<c>Sql.Def</c>, <c>WriteTransaction</c>, <c>SettingsReader</c>) only stay the
///     one implementation while nothing re-grows a private copy. One class, three facts sharing one scanner:
///     each rule is the same walk over <c>src</c> with a different pattern and allow-list, and a separate
///     fact per rule keeps a failure naming the rule that broke.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed partial class SqlHelperSourceGateTests
{
    [Fact]
    public void CommandDefinitionDef_IsDeclaredOnlyInSqlCs()
    {
        // A class-local Def(string, object? = null, CancellationToken = default) would shadow Sql.Def and
        // silently bind a CancellationToken as the Dapper parameters object (#826).
        var offenders = SourceFilesMatching(DefDeclaration(), "src", allowedFiles: ["Sql.cs"]);

        offenders.ShouldBeEmpty("only Sql.cs may declare a CommandDefinition Def( method; call Sql.Def instead.");
    }

    [Fact]
    public void BeginImmediate_IsWrittenOnlyInWriteTransactionAndReplace()
    {
        var offenders = SourceFilesMatching(BeginImmediateLiteral(), "src",
            allowedFiles: ["WriteTransaction.cs", "SqliteMemoryStore.Replace.cs"]);

        offenders.ShouldBeEmpty(
            "hand-written BEGIN IMMEDIATE belongs to WriteTransaction.cs (and Replace.cs, which times the lock wait); use InWriteTransactionAsync.");
    }

    [Fact]
    public void SettingValueRead_IsWrittenOnlyInSettingsReaderAndMemorySql()
    {
        // Scoped to Infrastructure, where SettingsReader lives, and to value reads: count/delete/LIKE
        // statements over settings (schema repair, ProjectIdsRepair) are not reads of one setting.
        var offenders = SourceFilesMatching(SettingValueSelect(), "src/AiRaccoon.Infrastructure",
            allowedFiles: ["SettingsReader.cs", "MemorySql.cs"]);

        offenders.ShouldBeEmpty("read a setting through connection.ReadSettingAsync, not a raw SELECT.");
    }

    [GeneratedRegex(@"CommandDefinition\s+Def\s*\(")]
    private static partial Regex DefDeclaration();

    [GeneratedRegex("\"\\s*BEGIN\\s+IMMEDIATE", RegexOptions.IgnoreCase)]
    private static partial Regex BeginImmediateLiteral();

    [GeneratedRegex(@"SELECT\s+value\s+FROM\s+settings\s+WHERE\s+key", RegexOptions.IgnoreCase)]
    private static partial Regex SettingValueSelect();

    private static List<string> SourceFilesMatching(Regex pattern, string directory, string[] allowedFiles)
    {
        var root = RepositoryRoot();
        return Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p) && !allowedFiles.Contains(Path.GetFileName(p), StringComparer.Ordinal))
            .Where(p => pattern.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AiRaccoon.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("AiRaccoon.slnx not found above the test binary");
        return dir.FullName;
    }
}
