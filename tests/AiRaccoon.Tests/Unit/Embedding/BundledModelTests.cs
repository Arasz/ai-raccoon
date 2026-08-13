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
            Directory.Delete(tempDir, true);
        }
    }
}
