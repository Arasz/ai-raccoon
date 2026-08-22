using System.Text;
using AiRaccoon.Infrastructure.Embedding.Download;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.Download;

/// <summary>
///     WP2 (D4 m6): external-data enumeration reads the ONNX protobuf's external_data entries —
///     the only honest way to know which sibling files a graph needs (bge-m3's model.onnx_data
///     trap). The models under test are generated in-test with a tiny protobuf writer, so no
///     binary fixtures are committed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class OnnxGraphProbeReaderTests
{
    [Fact]
    public void ReadsExternalDataLocations_FromGeneratedOnnx()
    {
        var model = TestOnnx.MinimalModelWithExternalData("model.onnx_data");

        var probe = Probe().Read(model);

        probe.ExternalDataFiles.ShouldBe(["model.onnx_data"]);
    }

    [Fact]
    public void ReadsGraphInputsAndOutputs()
    {
        var model = TestOnnx.MinimalModelWithExternalData("model.onnx_data");

        var probe = Probe().Read(model);

        probe.InputNames.ShouldBe(["input_ids", "attention_mask"]);
        probe.OutputNames.ShouldBe(["token_embeddings", "sentence_embedding"]);
    }

    [Fact]
    public void ReadsIrAndOpsetVersions()
    {
        var probe = Probe().Read(TestOnnx.MinimalModelWithExternalData("w.bin"));

        probe.IrVersion.ShouldBe(6);
        probe.OpsetVersion.ShouldBe(17);
    }

    [Fact]
    public void DeduplicatesExternalDataLocations_AcrossInitializers()
    {
        var model = TestOnnx.MinimalModel(
            initializers:
            [
                TestOnnx.TensorWithExternalData("w1", "model.onnx_data"),
                TestOnnx.TensorWithExternalData("w2", "model.onnx_data")
            ]);

        var probe = Probe().Read(model);

        probe.ExternalDataFiles.ShouldBe(["model.onnx_data"]);
    }

    [Fact]
    public void NoExternalData_YieldsEmptyList()
    {
        var probe = Probe().Read(TestOnnx.MinimalModel());

        probe.ExternalDataFiles.ShouldBeEmpty();
    }

    [Fact]
    public void InlineInitializer_WithExternalDataEntries_IsIgnored()
    {
        // data_location 0 (DEFAULT) means the weights are inline; stray external_data entries
        // must not be treated as required siblings.
        var model = TestOnnx.MinimalModel(initializers: [TestOnnx.TensorWithExternalData("w", "stray.bin", dataLocation: 0)]);

        var probe = Probe().Read(model);

        probe.ExternalDataFiles.ShouldBeEmpty();
    }

    [Fact]
    public void GarbageFile_Throws_WithActionableMessage()
    {
        var ex = Should.Throw<OnnxProbeException>(() => Probe().Read("not an onnx file"u8.ToArray()));

        ex.Message.ShouldContain("ONNX", Case.Sensitive);
    }

    [Fact]
    public void TruncatedFile_Throws_WithActionableMessage()
    {
        var full = TestOnnx.MinimalModelWithExternalData("model.onnx_data");

        Should.Throw<OnnxProbeException>(() => Probe().Read(full[..(full.Length / 2)]));
    }

    private static OnnxGraphProbeReader Probe() => new();
}

/// <summary>
///     Minimal hand-rolled ONNX ModelProto writer for tests (protobuf wire format): ir_version 6,
///     one graph, one or more initializers (with optional external_data), graph inputs
///     input_ids/attention_mask, outputs token_embeddings + sentence_embedding (or the named
///     outputs a caller passes — a graph that pools itself declares only last_hidden_state),
///     opset 17.
/// </summary>
internal static class TestOnnx
{
    public static byte[] MinimalModelWithExternalData(string externalLocation, IReadOnlyList<string>? outputs = null) =>
        MinimalModel(initializers: [TensorWithExternalData("weights", externalLocation)], outputs: outputs);

    public static byte[] MinimalModel(IReadOnlyList<byte[]>? initializers = null, IReadOnlyList<string>? outputs = null)
    {
        List<byte> graph = [.. StringField(1, "g")];
        foreach (var tensor in initializers ?? [])
        {
            graph.AddRange(MessageField(5, tensor));
        }

        foreach (var input in new[] { "input_ids", "attention_mask" })
        {
            graph.AddRange(MessageField(11, ValueInfo(input)));
        }

        foreach (var output in outputs ?? ["token_embeddings", "sentence_embedding"])
        {
            graph.AddRange(MessageField(12, ValueInfo(output)));
        }

        var opset = new List<byte>();
        opset.AddRange(StringField(1, string.Empty));
        opset.AddRange(Int64Field(2, 17));

        var model = new List<byte>();
        model.AddRange(Int64Field(1, 6)); // ir_version
        model.AddRange(StringField(2, "pytorch"));
        model.AddRange(MessageField(7, graph.ToArray()));
        model.AddRange(MessageField(8, opset.ToArray()));
        return model.ToArray();
    }

    public static byte[] TensorWithExternalData(string name, string location, int dataLocation = 1)
    {
        var tensor = new List<byte>();
        tensor.AddRange(Int64Field(1, 2)); // dims
        tensor.AddRange(Int64Field(1, 4));
        tensor.AddRange(Int64Field(2, 1)); // data_type FLOAT
        tensor.AddRange(StringField(3, name));
        tensor.AddRange(MessageField(13, StringStringEntry("location", location)));
        tensor.AddRange(MessageField(13, StringStringEntry("offset", "0")));
        tensor.AddRange(MessageField(13, StringStringEntry("length", "8")));
        tensor.AddRange(Int64Field(14, dataLocation)); // data_location
        return tensor.ToArray();
    }

    private static byte[] ValueInfo(string name) => StringField(1, name);

    private static byte[] StringStringEntry(string key, string value)
    {
        var entry = new List<byte>();
        entry.AddRange(StringField(1, key));
        entry.AddRange(StringField(2, value));
        return entry.ToArray();
    }

    private static byte[] Int64Field(int field, long value)
    {
        var bytes = new List<byte> { (byte)(field << 3 | 0) };
        bytes.AddRange(Varint((ulong)value));
        return bytes.ToArray();
    }

    private static byte[] StringField(int field, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var bytes = new List<byte> { (byte)(field << 3 | 2) };
        bytes.AddRange(Varint((ulong)utf8.Length));
        bytes.AddRange(utf8);
        return bytes.ToArray();
    }

    private static byte[] MessageField(int field, byte[] payload)
    {
        var bytes = new List<byte> { (byte)(field << 3 | 2) };
        bytes.AddRange(Varint((ulong)payload.Length));
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80)
        {
            bytes.Add((byte)(value | 0x80));
            value >>= 7;
        }

        bytes.Add((byte)value);
        return bytes.ToArray();
    }
}
