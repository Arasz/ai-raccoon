using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Production code reaches ONNX Runtime only through <c>OrtSessionLog</c>: one <c>OrtEnv.Instance()</c> that
///     runs first creates the environment without the log callback, and every session then logs "not reported"
///     instead of its node placement.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OrtSessionsGoThroughOrtSessionLogTests
{
    private const string Owner = "src/AiRaccoon.Infrastructure/Embedding/OrtSessionLog.cs";

    [Fact]
    public void Src_CreatesNoOrtEnvironmentOrSessionOutsideOrtSessionLog()
    {
        var root = RepositoryRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p))
            .Select(p => (Path: Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(p)))
            .Where(f => f.Path != Owner)
            .Where(f => f.Text.Contains("OrtEnv.Instance()", StringComparison.Ordinal)
                        || f.Text.Contains("new InferenceSession(", StringComparison.Ordinal))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.ShouldBeEmpty("use OrtSessionLog.Environment and OrtSessionLog.Create so the ORT log callback stays installed.");
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
