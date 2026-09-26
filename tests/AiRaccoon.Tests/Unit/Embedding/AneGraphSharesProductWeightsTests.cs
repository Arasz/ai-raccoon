using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Infrastructure.Embedding.Download;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0118 (#764 stage 3 P2): <c>model_fp16_ane.onnx</c> carries no weights of its own — it
///     reads <c>model_fp16.onnx_data</c> at the product graph's own offsets, so a changed product
///     weights file must fail this build rather than a user's session (ADR-0118 "What ships").
///     Reads only the two committed graph headers, never <c>model_fp16.onnx_data</c> itself, so it
///     runs on CI where that gitignored file is absent.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class AneGraphSharesProductWeightsTests
{
    private const string ModelDir = "src/AiRaccoon/Models/granite-embedding-small-english-r2";
    private const string ProductGraphRelativePath = ModelDir + "/model_fp16.onnx";
    private const string AneGraphRelativePath = ModelDir + "/model_fp16_ane.onnx";
    private const string ManifestRelativePath = ModelDir + "/ai-raccoon.manifest.json";
    private const string ProductDataFileName = "model_fp16.onnx_data";
    private const string AneGraphFileName = "model_fp16_ane.onnx";

    // The committed product model_fp16.onnx_data is ~97 MB; no real slice's offset can reach this,
    // so shifting one ref here is guaranteed to miss every entry in the product graph's own refs.
    private const long OutOfRangeOffset = 999_999_999L;

    [Fact]
    public void AneGraph_EveryExternalRef_SharesTheProductGraphsWeights()
    {
        var product = ProbeGraph(ProductGraphRelativePath);
        var ane = ProbeGraph(AneGraphRelativePath);

        VerifyAneRefsShareProductWeights(ane, product);
    }

    [Fact]
    public void AneGraph_ExternalRefWithAChangedOffset_IsDetectedAsNotSharingWeights()
    {
        var product = ProbeGraph(ProductGraphRelativePath);
        var ane = ProbeGraph(AneGraphRelativePath);
        var mutated = WithFirstOffsetChanged(ane);

        var ex = Should.Throw<ShouldAssertException>(() => VerifyAneRefsShareProductWeights(mutated, product));

        ex.Message.ShouldContain(mutated.ExternalDataRefs![0].InitializerName);
    }

    [Fact]
    public void Manifest_DoesNotListTheAneGraph()
    {
        var manifestJson = File.ReadAllText(TestData.RepoFile(ManifestRelativePath));

        VerifyManifestOmitsAneGraph(manifestJson);
    }

    [Fact]
    public void Manifest_ListingTheAneGraph_IsDetectedAsInvalid()
    {
        var manifestJson = File.ReadAllText(TestData.RepoFile(ManifestRelativePath));
        var mutated = WithAneGraphAddedToManifest(manifestJson);

        var ex = Should.Throw<ShouldAssertException>(() => VerifyManifestOmitsAneGraph(mutated));

        ex.Message.ShouldContain(AneGraphFileName);
    }

    /// <summary>Every external ref the ANE graph declares must read the product's own weights
    /// file, at a byte range the product graph itself uses — never a second copy.</summary>
    private static void VerifyAneRefsShareProductWeights(OnnxGraphProbe ane, OnnxGraphProbe product)
    {
        ane.ExternalDataRefs.ShouldNotBeNull();
        ane.ExternalDataRefs!.ShouldNotBeEmpty();
        var productSlices = product.ExternalDataRefs!.Select(r => (r.Offset, r.Length)).ToHashSet();

        foreach (var reference in ane.ExternalDataRefs!)
        {
            reference.Location.ShouldBe(ProductDataFileName,
                $"ANE initializer '{reference.InitializerName}' reads '{reference.Location}', not the product's own {ProductDataFileName}");
            productSlices.ShouldContain((reference.Offset, reference.Length),
                $"ANE initializer '{reference.InitializerName}' reads offset {reference.Offset}/length {reference.Length}, " +
                "which is not one of the product graph's own weight slices");
        }
    }

    /// <summary>model_fp16_ane.onnx must stay outside the manifest: the manifest is part of the
    /// engine fingerprint, so listing it there would force every bank to re-embed.</summary>
    private static void VerifyManifestOmitsAneGraph(string manifestJson)
    {
        using var doc = JsonDocument.Parse(manifestJson);
        var files = doc.RootElement.GetProperty("onnx").GetProperty("files")
            .EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();

        files.ShouldNotContain(AneGraphFileName,
            $"the manifest lists {AneGraphFileName}; that would add it to the engine fingerprint and force every bank to re-embed");
    }

    private static OnnxGraphProbe WithFirstOffsetChanged(OnnxGraphProbe probe)
    {
        var refs = probe.ExternalDataRefs!.ToList();
        refs[0] = refs[0] with { Offset = OutOfRangeOffset };
        return probe with { ExternalDataRefs = refs };
    }

    private static string WithAneGraphAddedToManifest(string manifestJson)
    {
        var node = JsonNode.Parse(manifestJson)!.AsObject();
        var files = node["onnx"]!["files"]!.AsArray();
        files.Add(new JsonObject { ["path"] = AneGraphFileName, ["sha256"] = new string('0', 64) });
        return node.ToJsonString();
    }

    private static OnnxGraphProbe ProbeGraph(string relativePath) =>
        new OnnxGraphProbeReader().Read(File.ReadAllBytes(TestData.RepoFile(relativePath)));
}
