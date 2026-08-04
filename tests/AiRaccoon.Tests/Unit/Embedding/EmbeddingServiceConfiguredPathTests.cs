using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     The merged EmbeddingModelPath (--embedding-model / AIRACCOON_EMBEDDING_MODEL) wins
///     over the bundled model when settings.Model is empty.
/// </summary>
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
    public void CreateGenerator_UsesConfiguredPath_WhenSettingsModelEmpty()
    {
        var custom = Path.Combine(_root, "custom.onnx");
        File.Copy(BundledModel.ResolveModelPath(), custom);

        var service = new EmbeddingService(new InfrastructureOptions { EmbeddingModelPath = custom });

        using var generator = service.CreateGenerator(new EmbeddingSettings("local", null, null, null));
        generator.ShouldNotBeNull();
    }

    [Fact]
    public void CreateGenerator_MissingConfiguredPath_Throws()
    {
        var service = new EmbeddingService(
            new InfrastructureOptions { EmbeddingModelPath = Path.Combine(_root, "missing.onnx") });

        Should.Throw<Exception>(() => service.CreateGenerator(new EmbeddingSettings("local", null, null, null)));
    }
}
