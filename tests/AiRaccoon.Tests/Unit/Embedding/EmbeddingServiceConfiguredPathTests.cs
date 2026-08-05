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

        var ex = Should.Throw<InvalidOperationException>(() =>
            service.CreateGenerator(new EmbeddingSettings("local", Path.Combine(_root, "missing.onnx"), null, null)));
        ex.Message.ShouldContain("ai-raccoon model set local");
    }

    [Fact]
    public void CreateGenerator_ModelNameInSettings_ThrowsActionableError()
    {
        var service = new EmbeddingService();

        // A model NAME (not a path) must fail fast with the resolved path and both remediation commands.
        var ex = Should.Throw<InvalidOperationException>(() =>
            service.CreateGenerator(new EmbeddingSettings("local", "nomic-embed-text", null, null)));
        ex.Message.ShouldContain(Path.GetFullPath("nomic-embed-text"));
        ex.Message.ShouldContain("model set local");
        ex.Message.ShouldContain("path-to-onnx");
    }
}
