using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Tests;

public static class TestData
{
    public static string CreateTempRoot(string prefix = "ai-raccoon-tests")
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    public static InfrastructureOptions CreateInfrastructureOptions(string dataRoot, string rid = "osx-arm64") =>
        new() { DataRoot = dataRoot, Rid = rid };

    /// <summary>Returns the p-th percentile (0–1) of the samples.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double quantile)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        var sorted = samples.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(quantile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    /// <summary>Cosine similarity between two float vectors.</summary>
    public static double Cosine(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
    {
        var aSpan = a.Span;
        var bSpan = b.Span;
        double dot = 0;
        for (var i = 0; i < aSpan.Length; i++)
        {
            dot += aSpan[i] * bSpan[i];
        }

        return dot;
    }
}
