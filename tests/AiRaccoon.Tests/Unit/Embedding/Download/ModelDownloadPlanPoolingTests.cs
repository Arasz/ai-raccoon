using AiRaccoon.Infrastructure.Embedding.Download;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Download;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModelDownloadPlanPoolingTests
{
    [Fact]
    public void GraphEmitsTokenEmbeddings_KeepsThePlannedPoolingMode()
    {
        var plan = PlannedClsMode();
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal) { ["last_hidden_state"] = 3 };

        var result = ModelDownloadPlanPooling.ApplyGraphOutputRanks(plan, ranks);

        result.PoolingMode.ShouldBe(PoolingMode.Cls);
        result.EmbeddingOutput.ShouldBeNull();
        result.PoolingProvenance.ShouldBe("placeholder");
    }

    [Fact]
    public void GraphEmitsPooledOutput_OverridesThePlannedPoolingMode()
    {
        var plan = PlannedClsMode();
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal) { ["last_hidden_state"] = 2 };

        var result = ModelDownloadPlanPooling.ApplyGraphOutputRanks(plan, ranks);

        result.PoolingMode.ShouldBe(PoolingMode.ModelOutput);
        result.EmbeddingOutput.ShouldBe("last_hidden_state");
        result.PoolingProvenance.ShouldBe("onnx-graph");
    }

    private static ModelDownloadPlan PlannedClsMode() => new(
        "test/model",
        "main",
        [],
        [],
        [],
        [],
        "model.onnx",
        TokenizerFamily.BertWordpiece,
        384,
        256,
        false,
        ["input_ids", "attention_mask"],
        null,
        "last_hidden_state",
        PoolingMode.Cls,
        NormalizationMode.L2,
        false,
        false,
        new Dictionary<string, int>(),
        "placeholder");
}
