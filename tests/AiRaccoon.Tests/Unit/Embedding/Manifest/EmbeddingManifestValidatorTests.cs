using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Manifest;

/// <summary>
///     WP1 (D5 + architecture §5.2): every manifest validation rule fires with an actionable
///     message. The golden malformed fixtures each carry exactly one defect, so each test proves
///     one rule.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class EmbeddingManifestValidatorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "ManifestFixtures", name);

    private static EmbeddingManifest Parse(string name) =>
        new EmbeddingManifestSerializer().Deserialize(File.ReadAllText(FixturePath(name)));

    private static EmbeddingManifest BgeM3() =>
        new EmbeddingManifestSerializer().Deserialize(File.ReadAllText(FixturePath("bge-m3.full.json")));

    [Fact]
    public void ValidBgeM3Manifest_HasNoErrors()
    {
        new EmbeddingManifestValidator().Validate(BgeM3()).ShouldBeEmpty();
    }

    [Fact]
    public void BadDimensions_Rejected_WithActionableMessage()
    {
        var manifest = BgeM3() with { Dimensions = 0 };

        var errors = new EmbeddingManifestValidator().Validate(manifest);
        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("dimensions");
        errors[0].ShouldContain("0");
        errors[0].ShouldContain("positive");
    }

    [Fact]
    public void NegativeDimensions_Rejected()
    {
        var errors = new EmbeddingManifestValidator().Validate(BgeM3() with { Dimensions = -5 });
        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("dimensions");
    }

    [Fact]
    public void MalformedSha256_Rejected_WithActionableMessage()
    {
        // The golden fixture pins one defect: tokenizer file sha256 "not-a-sha256".
        var errors = new EmbeddingManifestValidator().Validate(Parse("malformed/bad-sha.json"));
        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("tokenizer.files[0].sha256");
        errors[0].ShouldContain("64");
    }

    [Fact]
    public void TooShortSha256_Rejected()
    {
        var manifest = BgeM3() with
        {
            Tokenizer = BgeM3().Tokenizer with
            {
                Files = [new ManifestFile("vocab.txt", "abc123")]
            }
        };

        var errors = new EmbeddingManifestValidator().Validate(manifest);
        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("sha256");
    }

    [Fact]
    public void EmptyFileList_ForLocalProvider_Rejected_WithActionableMessage()
    {
        var errors = new EmbeddingManifestValidator().Validate(Parse("malformed/empty-files-local.json"));

        errors.Count.ShouldBe(2, "both the tokenizer file list and the onnx file list are empty for provider 'local'");
        errors.ShouldContain(e => e.Contains("tokenizer.files", StringComparison.Ordinal) && e.Contains("at least one", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("onnx.files", StringComparison.Ordinal) && e.Contains("at least one", StringComparison.Ordinal));
    }

    [Fact]
    public void ModelOutput_WithoutEmbeddingOutput_Rejected_WithActionableMessage()
    {
        var errors = new EmbeddingManifestValidator().Validate(Parse("malformed/model-output-without-embedding-output.json"));

        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("pooling.mode");
        errors[0].ShouldContain("model-output");
        errors[0].ShouldContain("onnx.embeddingOutput");
    }

    [Fact]
    public void ClsOrMean_WithoutTokenEmbeddingsOutput_Rejected()
    {
        var manifest = BgeM3() with
        {
            Pooling = BgeM3().Pooling with { Mode = PoolingMode.Cls },
            Onnx = BgeM3().Onnx with { TokenEmbeddingsOutput = null }
        };

        var errors = new EmbeddingManifestValidator().Validate(manifest);
        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("pooling.mode");
        errors[0].ShouldContain("cls");
        errors[0].ShouldContain("onnx.tokenEmbeddingsOutput");
    }

    [Fact]
    public void MrlSupported_WithoutMinDimensions_Rejected_WithActionableMessage()
    {
        var errors = new EmbeddingManifestValidator().Validate(Parse("malformed/mrl-without-min-dims.json"));

        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("mrl.supported");
        errors[0].ShouldContain("mrl.minDimensions");
    }

    [Fact]
    public void MrlSupported_WithMinDimensions_Passes()
    {
        var manifest = BgeM3() with { MRL = new MRLInfo(true, 32) };
        new EmbeddingManifestValidator().Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void UnknownFamily_Rejected_EvenWhenEnumValueIsUndefined()
    {
        var manifest = BgeM3() with
        {
            Tokenizer = BgeM3().Tokenizer with { Family = (TokenizerFamily)99 }
        };

        var errors = new EmbeddingManifestValidator().Validate(manifest);
        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("tokenizer.family");
    }

    [Fact]
    public void TokenizerJson_Rejected_WithD5GateMessage()
    {
        // D5: tokenizer-json is gated on an ML.Tokenizers capability check (deferred) — the
        // manifest contract rejects it until the engine can actually consume HF tokenizer.json.
        var errors = new EmbeddingManifestValidator().Validate(Parse("malformed/tokenizer-json-deferred.json"));

        errors.ShouldHaveSingleItem();
        errors[0].ShouldContain("tokenizer-json");
        errors[0].ShouldContain("not yet supported");
    }

    [Fact]
    public void OpenaiProvider_AllowsEmptyFileLists()
    {
        // Remote manifests describe a settings-row engine: there are no files to pin or verify.
        var manifest = BgeM3() with
        {
            Provider = ManifestProvider.Openai,
            Tokenizer = BgeM3().Tokenizer with { Files = [] },
            Onnx = BgeM3().Onnx with { Files = [] }
        };

        new EmbeddingManifestValidator().Validate(manifest).ShouldBeEmpty();
    }

    [Fact]
    public void EveryError_NamesTheOffendingFieldPath()
    {
        var manifests = new (EmbeddingManifest Manifest, string Field)[]
        {
            (BgeM3() with { Dimensions = 0 }, "dimensions"),
            (BgeM3() with { ContextWindowTokens = 0 }, "contextWindowTokens"),
            (BgeM3() with { Model = " " }, "model"),
            (BgeM3() with { Source = new ManifestSource(" ", "main") }, "source.repo")
        };

        foreach (var (manifest, field) in manifests)
        {
            var errors = new EmbeddingManifestValidator().Validate(manifest);
            errors.ShouldNotBeEmpty();
            errors.ShouldAllBe(e => e.Contains(field, StringComparison.Ordinal),
                $"expected an error naming '{field}', got: {string.Join("; ", errors)}");
        }
    }
}
