using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Layering;

/// <summary>
///     ADR-0113: AiRaccoon is an application with no <see cref="SynchronizationContext" />, so
///     <c>ConfigureAwait</c> changes nothing and is not written anywhere in src or tests.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoConfigureAwaitTests
{
    [Fact]
    public void ConfigureAwait_IsNotCalledInSourceOrTests()
    {
        var root = RepositoryRoot();
        var offenders = new[] { "src", "tests" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(p => !IsBuildOutput(p) && !p.EndsWith(nameof(NoConfigureAwaitTests) + ".cs", StringComparison.Ordinal))
            .Where(p => File.ReadAllText(p).Contains(".ConfigureAwait(", StringComparison.Ordinal))
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("ADR-0113: application code awaits directly; drop the ConfigureAwait call.");
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
