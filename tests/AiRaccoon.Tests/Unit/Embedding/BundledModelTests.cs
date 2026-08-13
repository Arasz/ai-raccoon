using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

public sealed class BundledModelTests
{
    [Fact]
    public void ResolveModelPath_FindsModelFlatNextToTool()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);
        try
        {
            var fileName = BundledModel.ModelFileName;
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
