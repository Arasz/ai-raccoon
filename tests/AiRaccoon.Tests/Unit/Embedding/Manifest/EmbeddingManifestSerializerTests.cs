using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Manifest;

/// <summary>
///     WP1 (arbitrary-embedding-models plan, D1): the manifest (de)serializer pins the ONE
///     approved schema — camelCase field names, kebab-case enum values, explicit nulls for the
///     nullable fields, and golden-fixture round-trips. The full bge-m3 fixture is the schema
///     pin: parsing it must yield exactly the constructed manifest, and re-serializing must be
///     byte-stable.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class EmbeddingManifestSerializerTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Resources", "ManifestFixtures", name);

    private static string ReadFixture(string name) => File.ReadAllText(FixturePath(name));

    /// <summary>The full bge-m3 manifest exactly as approved (plan §5.6 + D1 rev-2 amendments).</summary>
    private static EmbeddingManifest BgeM3() => new(
        ManifestVersion: 1,
        Model: "BAAI/bge-m3",
        Source: new ManifestSource("BAAI/bge-m3", "main"),
        Provider: ManifestProvider.Local,
        Dimensions: 1024,
        ContextWindowTokens: 8192,
        Normalization: NormalizationMode.L2,
        QueryInstruction: null,
        RequiresTokenTypeIds: false,
        MRL: new MRLInfo(false, null),
        Pooling: new PoolingManifest(PoolingMode.ModelOutput,
            new PoolingOutputNames("sentence_embedding", "token_embeddings")),
        Tokenizer: new TokenizerManifest(TokenizerFamily.SentencePiece,
        [
            new ManifestFile("sentencepiece.bpe.model",
                "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865")
        ],
            new TokenizerOptionsManifest(true, true, new Dictionary<string, int>
            {
                ["<s>"] = 0, ["<pad>"] = 1, ["</s>"] = 2, ["<unk>"] = 3
            })),
        Onnx: new OnnxManifest(["input_ids", "attention_mask"], "sentence_embedding", "token_embeddings",
        [
            new ManifestFile("model.onnx",
                "f84251230831afb359ab26d9fd37d5936d4d9bb5d1d5410e66442f630f24435b"),
            new ManifestFile("model.onnx_data",
                "1eebfb28493f67bba03ce0ef64bfdc7fc5a3bd9d7493f818bb1d78cd798416b4")
        ]));

    [Fact]
    public void FullBgeM3Fixture_Parses_AndRoundTrips_ByteStable()
    {
        var parsed = new EmbeddingManifestSerializer().Deserialize(ReadFixture("bge-m3.full.json"));

        new EmbeddingManifestSerializer().Serialize(parsed).ShouldBe(
            new EmbeddingManifestSerializer().Serialize(BgeM3()),
            "the golden fixture must parse to exactly the approved manifest (field names, kebab enums, explicit nulls)");
    }

    [Fact]
    public void Serialize_UsesThePinnedSchema_FieldNamesAndKebabEnums()
    {
        var json = new EmbeddingManifestSerializer().Serialize(BgeM3());

        json.ShouldContain("\"manifestVersion\": 1");
        json.ShouldContain("\"contextWindowTokens\": 8192");
        json.ShouldContain("\"requiresTokenTypeIds\": false");
        json.ShouldContain("\"queryInstruction\": null");
        json.ShouldContain("\"minDimensions\": null");
        json.ShouldContain("\"specialTokens\"");
        json.ShouldContain("\"tokenEmbeddingsOutput\"");
        // kebab-case enum values — the schema spells them mean|cls|model-output|last-token and
        // bert-wordpiece|sentencepiece|tokenizer-json, never camelCase C# names.
        json.ShouldContain("\"mode\": \"model-output\"");
        json.ShouldContain("\"family\": \"sentencepiece\"");
        json.ShouldContain("\"normalization\": \"l2\"");
        json.ShouldContain("\"provider\": \"local\"");
        json.ShouldNotContain("modelOutput", Case.Sensitive);
        json.ShouldNotContain("sentencePiece", Case.Sensitive);
    }

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var original = BgeM3();

        var reparsed = new EmbeddingManifestSerializer().Deserialize(new EmbeddingManifestSerializer().Serialize(original));

        new EmbeddingManifestSerializer().Serialize(reparsed).ShouldBe(new EmbeddingManifestSerializer().Serialize(original));
        reparsed.Dimensions.ShouldBe(1024);
        reparsed.ContextWindowTokens.ShouldBe(8192);
        reparsed.Tokenizer.Family.ShouldBe(TokenizerFamily.SentencePiece);
        reparsed.Tokenizer.Options!.SpecialTokens.ShouldBe(original.Tokenizer.Options!.SpecialTokens);
        reparsed.Tokenizer.Options!.AddBeginOfSentence.ShouldBeTrue();
        reparsed.Tokenizer.Options!.AddEndOfSentence.ShouldBeTrue();
        reparsed.Onnx.Inputs.ShouldBe(["input_ids", "attention_mask"]);
        reparsed.Onnx.EmbeddingOutput.ShouldBe("sentence_embedding");
        reparsed.Onnx.TokenEmbeddingsOutput.ShouldBe("token_embeddings");
        reparsed.Pooling.Mode.ShouldBe(PoolingMode.ModelOutput);
        reparsed.Pooling.OutputNames!.Embedding.ShouldBe("sentence_embedding");
        reparsed.Pooling.OutputNames!.TokenEmbeddings.ShouldBe("token_embeddings");
        reparsed.Source.Repo.ShouldBe("BAAI/bge-m3");
        reparsed.Source.Revision.ShouldBe("main");
        reparsed.MRL.ShouldBe(new MRLInfo(false, null));
        reparsed.RequiresTokenTypeIds.ShouldBeFalse();
        reparsed.QueryInstruction.ShouldBeNull();
        reparsed.Provider.ShouldBe(ManifestProvider.Local);
    }

    [Fact]
    public void NullManifestLegacy_SemanticsAreDocumented()
    {
        // A model directory without manifest.json keeps the pre-manifest custom-path semantics
        // (plan §9: legacy .onnx path — WordPiece, bundled vocab, 384 dims, mean pooling, 256 ctx).
        LegacyManifestSemantics.LegacyTokenizerFamily.ShouldBe(TokenizerFamily.BertWordpiece);
        LegacyManifestSemantics.LegacyDimensions.ShouldBe(384);
        LegacyManifestSemantics.LegacyContextWindowTokens.ShouldBe(256);
        LegacyManifestSemantics.LegacyPoolingMode.ShouldBe(PoolingMode.Mean);
        LegacyManifestSemantics.LegacyNormalization.ShouldBe(NormalizationMode.L2);
        LegacyManifestSemantics.LegacyRequiresTokenTypeIds.ShouldBeTrue();
    }

    [Fact]
    public void UnknownFamily_FailsDeserialization_WithActionableMessage()
    {
        var ex = Should.Throw<EmbeddingManifestFormatException>(
            () => new EmbeddingManifestSerializer().Deserialize(ReadFixture("malformed/unknown-family.json")));

        ex.Message.ShouldContain("tokenizer.family");
        ex.Message.ShouldContain("tiktoken");
        ex.Message.ShouldContain("bert-wordpiece");
    }

    [Fact]
    public void UnknownPoolingMode_FailsDeserialization_WithActionableMessage()
    {
        var json = ReadFixture("bge-m3.full.json").Replace("\"mode\": \"model-output\"", "\"mode\": \"attention\"");
        var ex = Should.Throw<EmbeddingManifestFormatException>(
            () => new EmbeddingManifestSerializer().Deserialize(json));

        ex.Message.ShouldContain("pooling.mode");
        ex.Message.ShouldContain("attention");
    }

    [Fact]
    public void MalformedJson_FailsDeserialization_WithFormatException()
    {
        Should.Throw<EmbeddingManifestFormatException>(
            () => new EmbeddingManifestSerializer().Deserialize(ReadFixture("malformed/not-json.json")));
    }

    [Fact]
    public void WrongType_FailsDeserialization_WithFormatException()
    {
        var json = ReadFixture("bge-m3.full.json").Replace("\"dimensions\": 1024", "\"dimensions\": \"many\"");
        var ex = Should.Throw<EmbeddingManifestFormatException>(
            () => new EmbeddingManifestSerializer().Deserialize(json));

        ex.Message.ShouldContain("dimensions");
    }

    [Fact]
    public void MissingRequiredField_FailsDeserialization_WithFormatException()
    {
        var json = ReadFixture("bge-m3.full.json").Replace("\"provider\": \"local\",", string.Empty);

        var ex = Should.Throw<EmbeddingManifestFormatException>(
            () => new EmbeddingManifestSerializer().Deserialize(json));

        ex.Message.ShouldContain("provider");
    }
}
