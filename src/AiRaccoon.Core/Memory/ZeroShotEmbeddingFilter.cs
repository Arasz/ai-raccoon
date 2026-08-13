using System;
using System.Collections.Generic;
using System.Linq;

namespace AiRaccoon.Core.Memory;

public static class ZeroShotEmbeddingFilter
{
    // Simple cosine distance function
    public static double CosineDistance(float[] v1, float[] v2)
    {
        if (v1.Length != v2.Length)
        {
            throw new ArgumentException("Vectors must have same length");
        }
        double dot = 0, n1 = 0, n2 = 0;
        for (int i = 0; i < v1.Length; i++)
        {
            dot += v1[i] * v2[i];
            n1 += v1[i] * v1[i];
            n2 += v2[i] * v2[i];
        }
        if (n1 == 0 || n2 == 0)
        {
            return 1.0; // max distance if zero vector
        }
        return 1.0 - (dot / (Math.Sqrt(n1) * Math.Sqrt(n2)));
    }

    public static bool IsNoise(float[] documentEmbedding, float[] noiseLabelEmbedding, float threshold = 0.5f)
    {
        var distance = CosineDistance(documentEmbedding, noiseLabelEmbedding);
        return distance < threshold;
    }
}
