using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The local-engine guard rejects a missing model path before any ONNX work; a model NAME must fail actionably.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingServiceLocalGuardTests : IDisposable
{
    private readonly string _root = TestData.CreateTempRoot("airaccoon-embedding-guard-tests");

    public void Dispose()
    {
        TestData.DeleteTempRoot(_root);
    }

    [Fact]
    public void CreateGenerator_MissingSettingsModelPath_Throws()
    {
        var service = TestData.CreateEmbeddingService();

        var ex = Should.Throw<InvalidOperationException>(() =>
            service.CreateGenerator(new EmbeddingSettings("local", Path.Combine(_root, "missing.onnx"), null, null)));
        ex.Message.ShouldContain("ai-raccoon model embedding set local");
    }

    [Fact]
    public void CreateGenerator_ModelNameInSettings_ThrowsActionableError()
    {
        var service = TestData.CreateEmbeddingService();

        // A model NAME (not a path) must fail fast with the resolved path and both remediation commands.
        var ex = Should.Throw<InvalidOperationException>(() =>
            service.CreateGenerator(new EmbeddingSettings("local", "nomic-embed-text", null, null)));
        ex.Message.ShouldContain(Path.GetFullPath("nomic-embed-text"));
        ex.Message.ShouldContain("model embedding set local");
        ex.Message.ShouldContain("path-to-onnx");
    }
}
