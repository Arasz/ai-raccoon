using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Gate: the bundled int8 ONNX model (and its BERT vocab) must exist and verify after
///     bootstrap; fails rather than skips when missing or mismatched. The default engine
///     must ship inside the tool package (docs/work/features-native-memory/native-memory.feature).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BundledModelGateTests
{
    [Fact]
    public async Task BundledModel_IsPresentAndShaVerified_AfterBootstrap()
    {
        var result = await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

        result.Errors.ShouldBeEmpty(
            "bundled embedding model missing or mismatched; run scripts/download-embedding-model.py " +
            "with network access or place a verified copy in src/AiRaccoon/Models/");

        var modelPath = BundledModel.ResolveModelPath();
        File.Exists(modelPath).ShouldBeTrue(modelPath);
        BundledResource.Sha256Of(modelPath).ShouldBe(BundledModel.ModelSha256, StringCompareShould.IgnoreCase);

        var vocabPath = BundledModel.ResolveVocabPath();
        File.Exists(vocabPath).ShouldBeTrue(vocabPath);
        BundledResource.Sha256Of(vocabPath).ShouldBe(BundledModel.VocabSha256, StringCompareShould.IgnoreCase);
    }
}
