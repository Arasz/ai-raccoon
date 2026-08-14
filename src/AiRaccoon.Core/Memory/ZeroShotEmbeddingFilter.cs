namespace AiRaccoon.Core.Memory;

/// <summary>
///     Cosine-distance math shared by the noise-learning subsystem (ADR-0039). The single-threshold
///     classifier this file originally also carried (<c>IsNoise</c>, three hardcoded anchor vectors)
///     measured 0/50 recall (ADR-0033) and is not restored — see docs/adr/0039.
/// </summary>
public static class ZeroShotEmbeddingFilter
{
    public static double CosineDistance(float[] v1, float[] v2)
    {
        if (v1.Length != v2.Length)
        {
            throw new ArgumentException("Vectors must have same length");
        }

        double dot = 0, n1 = 0, n2 = 0;
        for (var i = 0; i < v1.Length; i++)
        {
            dot += v1[i] * v2[i];
            n1 += v1[i] * v1[i];
            n2 += v2[i] * v2[i];
        }

        if (n1 == 0 || n2 == 0)
        {
            return 1.0; // max distance if zero vector
        }

        return 1.0 - dot / (Math.Sqrt(n1) * Math.Sqrt(n2));
    }
}
