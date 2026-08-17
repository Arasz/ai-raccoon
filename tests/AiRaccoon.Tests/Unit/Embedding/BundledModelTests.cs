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
            const string fileName = BundledModel.ModelFileName;
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
    public void ResolveModelPath_WithNonexistentBaseDirectory_BlamesTheReplacedInstall_NotMissingAsset()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

        var ex = Should.Throw<BundledModelInstallReplacedException>(
            () => BundledModel.ResolveModelPath(null, missingDir));

        ex.Message.ShouldContain(missingDir);
        ex.Message.ShouldNotContain("model set local");
    }

    [Fact]
    public void ResolveModelPath_WithExistingEmptyBaseDirectory_KeepsTheModelSetLocalMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var ex = Should.Throw<InvalidOperationException>(() => BundledModel.ResolveModelPath(null, tempDir));

            ex.ShouldNotBeOfType<BundledModelInstallReplacedException>();
            ex.Message.ShouldBe(BundledModel.MissingBundledModelMessage(BundledModel.ModelFileName));
        }
        finally
        {
            TestData.DeleteTempRoot(tempDir);
        }
    }

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
