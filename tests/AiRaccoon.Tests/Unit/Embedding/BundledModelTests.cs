using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BundledModelTests
{
    [Fact]
    public void ResolveModelPath_FindsModelFlatNextToTool()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            const string fileName = TestData.MiniLmModelFileName;
            var flatPath = Path.Combine(tempDir, fileName);
            File.WriteAllText(flatPath, "dummy");

            var resolved = BundledModel.ResolveBundled(fileName, tempDir);
            resolved.ShouldBe(flatPath);
        }
        finally
        {
            TestData.DeleteTempRoot(tempDir);
        }
    }

    [Fact]
    public void ResolveDirectory_FindsTheBundledManifestDirectory_AboveTheTool()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var bundled = Path.Combine(root, "Models", BundledModel.DirectoryName);
        var toolDir = Path.Combine(root, "tools", "net10.0");
        Directory.CreateDirectory(bundled);
        Directory.CreateDirectory(toolDir);
        try
        {
            File.WriteAllText(Path.Combine(bundled, "ai-raccoon.manifest.json"), "{}");

            BundledModel.ResolveDirectory(toolDir).ShouldBe(bundled);
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    [Fact]
    public void ResolveDirectory_WithNonexistentBaseDirectory_BlamesTheReplacedInstall_NotMissingAsset()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var ex = Should.Throw<BundledModelInstallReplacedException>(() => BundledModel.ResolveDirectory(missingDir));

        ex.Message.ShouldContain(missingDir);
    }

    [Fact]
    public void ResolveDirectory_WithExistingEmptyBaseDirectory_NamesTheMissingBundledDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var ex = Should.Throw<InvalidOperationException>(() => BundledModel.ResolveDirectory(tempDir));

            ex.ShouldNotBeOfType<BundledModelInstallReplacedException>();
            ex.Message.ShouldContain(BundledModel.DirectoryName);
        }
        finally
        {
            TestData.DeleteTempRoot(tempDir);
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("bundled", true)]
    [InlineData("BUNDLED", true)]
    [InlineData("/models/granite", false)]
    public void IsBundled_NamesTheBundledEngine_ForUnsetOrTheSettingValue(string? model, bool expected) =>
        BundledModel.IsBundled(model).ShouldBe(expected);

    [Fact]
    public void ResolveVocabPath_WithNonexistentBaseDirectory_BlamesTheReplacedInstall_NotMissingAsset()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var ex = Should.Throw<BundledModelInstallReplacedException>(
            () => BundledModel.ResolveVocabPath(missingDir));

        ex.Message.ShouldContain(missingDir);
        ex.Message.ShouldNotContain("model set local");
    }

    [Fact]
    public void ResolveVocabPath_WithExistingEmptyBaseDirectory_KeepsTheModelSetLocalMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var ex = Should.Throw<InvalidOperationException>(() => BundledModel.ResolveVocabPath(tempDir));

            ex.ShouldNotBeOfType<BundledModelInstallReplacedException>();
            ex.Message.ShouldBe(BundledModel.MissingBundledVocabMessage("vocab.txt"));
        }
        finally
        {
            TestData.DeleteTempRoot(tempDir);
        }
    }
}
