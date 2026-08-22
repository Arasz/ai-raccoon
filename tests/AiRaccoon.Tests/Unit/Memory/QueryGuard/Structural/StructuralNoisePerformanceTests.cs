using System;
using System.Diagnostics;
using System.Linq;
using AiRaccoon.Core.Memory.QueryGuard.Structural;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard.Structural;

/// <summary>
///     Acceptance criterion 2 (docs/adr/0041): p50 cost of one detector call — feature extraction
///     plus the 200-tree score — must land under 0.2ms. The record measured 0.081ms p50 on
///     query-length text (111 chars mean) and 0.107ms on write-length content (983 chars mean),
///     both with compress_ratio dropped (F5/F7); this test uses realistic query-length text, the
///     population the read-path guard actually scores.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Trait(TestCategories.Performance, TestCategories.Benchmark)]
public class StructuralNoisePerformanceTests
{
    private static readonly string[] SampleQueries =
    [
        "why did the auth build start failing",
        "[IMPORTANT: Background process proc_97aa3ea5eb50 completed normally (exit code 0).\nCommand: dotnet test\nOutput:\n]",
        "memory source normalization spike results",
        "info: something happened\n at System.Foo.Bar(baz)",
        "shared tier promotion queue hygiene, follow-through grading and dedup",
    ];

    [Fact]
    public void ExtractAndScore_P50CostIsUnderTwoHundredMicroseconds()
    {
        const int iterations = 5_000;

        // Warm up JIT.
        foreach (var q in SampleQueries)
        {
            StructuralNoiseModel.Score(StructuralNoiseFeatures.Extract(q));
        }

        var timings = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var query = SampleQueries[i % SampleQueries.Length];
            var sw = Stopwatch.StartNew();
            StructuralNoiseModel.Score(StructuralNoiseFeatures.Extract(query));
            sw.Stop();
            timings[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);
        var p50 = timings[iterations / 2];

        p50.ShouldBeLessThan(0.2, $"p50 was {p50:F4}ms over {iterations} iterations");
    }
}
