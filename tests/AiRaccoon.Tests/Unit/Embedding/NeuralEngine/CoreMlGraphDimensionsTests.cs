using System.Text;
using AiRaccoon.Infrastructure.Embedding.NeuralEngine;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding.NeuralEngine;

/// <summary>
///     The free-dimension overrides name the committed ANE graph's own symbolic input dimensions; a
///     re-export that renames one would otherwise leave that dimension dynamic, which CoreML refuses.
///     Reads only the committed graph's header, never the gitignored weights.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CoreMlGraphDimensionsTests
{
    private const string AneGraph = "src/AiRaccoon/Models/granite-embedding-small-english-r2/" + CoreMlGraph.FileName;

    [Fact]
    public void InputIds_AreBatchBySequence()
    {
        var inputs = ReadInputDimensions();

        inputs["input_ids"].ShouldBe([CoreMlGraph.BatchDimension, CoreMlGraph.SequenceDimension]);
    }

    [Fact]
    public void AttentionMask_IsBatchByTotalSequence()
    {
        var inputs = ReadInputDimensions();

        inputs["attention_mask"].ShouldBe([CoreMlGraph.BatchDimension, CoreMlGraph.MaskSequenceDimension]);
    }

    [Fact]
    public void GraphHasNoOtherInputs() =>
        ReadInputDimensions().Keys.Order(StringComparer.Ordinal).ShouldBe(["attention_mask", "input_ids"]);

    private static Dictionary<string, string[]> ReadInputDimensions()
    {
        var model = File.ReadAllBytes(TestData.RepoFile(AneGraph));
        var inputs = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var graph in Fields(model, 7))
        {
            foreach (var input in Fields(graph, 11))
            {
                var name = Encoding.UTF8.GetString(Fields(input, 1).Single());
                var dims = Fields(input, 2)
                    .SelectMany(type => Fields(type, 1))
                    .SelectMany(tensor => Fields(tensor, 2))
                    .SelectMany(shape => Fields(shape, 1))
                    .Select(dim => Fields(dim, 2).Select(p => Encoding.UTF8.GetString(p)).SingleOrDefault() ?? "static")
                    .ToArray();
                inputs[name] = dims;
            }
        }

        return inputs;
    }

    /// <summary>Every length-delimited value of <paramref name="field" /> in a protobuf message; other wire types are skipped.</summary>
    private static List<byte[]> Fields(byte[] message, int field)
    {
        var found = new List<byte[]>();
        var position = 0;
        while (position < message.Length)
        {
            var tag = Varint(message, ref position);
            var wireType = (int)(tag & 7);
            switch (wireType)
            {
                case 0:
                    Varint(message, ref position);
                    break;
                case 1:
                    position += 8;
                    break;
                case 2:
                    var length = (int)Varint(message, ref position);
                    if ((int)(tag >> 3) == field)
                    {
                        found.Add(message[position..(position + length)]);
                    }

                    position += length;
                    break;
                case 5:
                    position += 4;
                    break;
                default:
                    throw new InvalidDataException($"unsupported wire type {wireType}");
            }
        }

        return found;
    }

    private static ulong Varint(byte[] message, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = message[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }
    }
}
