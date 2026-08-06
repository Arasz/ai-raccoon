using System.Buffers.Binary;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>float vector ⇄ vec0 blob (little-endian IEEE-754 float32, one element per 4 bytes).</summary>
internal static class EmbeddingBlob
{
    public static byte[] ToBytes(ReadOnlyMemory<float> vector)
    {
        var blob = new byte[vector.Length * sizeof(float)];
        var span = vector.Span;
        for (var i = 0; i < span.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(blob.AsSpan(i * sizeof(float)), span[i]);
        }

        return blob;
    }

    public static float[] ToFloats(ReadOnlyMemory<byte> blob)
    {
        var vector = new float[blob.Length / sizeof(float)];
        var span = blob.Span;
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(i * sizeof(float)));
        }

        return vector;
    }
}
