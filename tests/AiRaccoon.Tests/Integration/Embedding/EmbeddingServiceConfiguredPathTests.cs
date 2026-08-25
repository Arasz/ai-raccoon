using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>The settings row embedding.model (written by `model set local &lt;path&gt;`) overrides the bundled model.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EmbeddingServiceConfiguredPathTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("airaccoon-embedding-path-tests");

    public void Dispose()
    {
        TestData.DeleteTempRoot(_root);
    }

    [RetryFact]
    public void CreateGenerator_UsesSettingsModelPath()
    {
        var custom = Path.Combine(_root, "custom.onnx");
        File.Copy(BundledModel.ResolveModelPath(), custom);

        var service = TestData.CreateEmbeddingService();

        using var generator = service.CreateGenerator(new EmbeddingSettings("local", custom, null, null));
        generator.ShouldNotBeNull();
    }
}
