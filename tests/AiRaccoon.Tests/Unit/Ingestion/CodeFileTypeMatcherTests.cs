using AiRaccoon.Infrastructure.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>
///     WP3-T01/T02 (docs/work/2026-08-21-code-search-moe-qa.md): code extensions route to the code
///     path, case-insensitively; docs/unsupported extensions do not.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CodeFileTypeMatcherTests
{
    private readonly CodeFileTypeMatcher _matcher = new();

    [Theory]
    [InlineData("Program.cs")]
    [InlineData("main.py")]
    [InlineData("app.ts")]
    [InlineData("server.go")]
    [InlineData("lib.rs")]
    public void IsCodeFile_CodeExtensions_ReturnsTrue(string path) => _matcher.IsCodeFile(path).ShouldBeTrue();

    [Theory]
    [InlineData("README.md")]
    [InlineData("notes.markdown")]
    [InlineData("data.json")]
    [InlineData("plain.txt")]
    public void IsCodeFile_MemoryExtensions_ReturnsFalse(string path) => _matcher.IsCodeFile(path).ShouldBeFalse();

    [Theory]
    [InlineData("logo.png")]
    [InlineData("archive.zip")]
    [InlineData("noext")]
    public void IsCodeFile_UnsupportedExtensions_ReturnsFalse(string path) => _matcher.IsCodeFile(path).ShouldBeFalse();

    [Fact]
    public void IsCodeFile_UppercaseExtension_MatchesCaseInsensitively()
    {
        _matcher.IsCodeFile("README.CS").ShouldBeTrue();
        _matcher.IsCodeFile("Main.Py").ShouldBeTrue();
    }

    [Fact]
    public void IsCodeFile_NullOrWhitespacePath_ReturnsFalse()
    {
        _matcher.IsCodeFile(string.Empty).ShouldBeFalse();
        _matcher.IsCodeFile("   ").ShouldBeFalse();
    }
}
