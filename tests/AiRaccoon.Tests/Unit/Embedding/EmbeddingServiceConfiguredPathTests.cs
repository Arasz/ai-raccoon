using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The settings row embedding.model (written by `model set local &lt;path&gt;`) overrides the bundled model.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EmbeddingServiceConfiguredPathTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("airaccoon-embedding-path-tests");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    [Fact]
    public void CreateGenerator_UsesSettingsModelPath()
    {
        var custom = Path.Combine(_root, "custom.onnx");
        File.Copy(BundledModel.ResolveModelPath(), custom);

        var service = new EmbeddingService();

        using var generator = service.CreateGenerator(new EmbeddingSettings("local", custom, null, null));
        generator.ShouldNotBeNull();
    }

    [Fact]
    public void CreateGenerator_MissingSettingsModelPath_Throws()
    {
        var service = new EmbeddingService();

        Should.Throw<Exception>(() =>
            service.CreateGenerator(new EmbeddingSettings("local", Path.Combine(_root, "missing.onnx"), null, null)));
    }
}
