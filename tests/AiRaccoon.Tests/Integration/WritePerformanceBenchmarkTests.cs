using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.Filtering.Policies;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Models;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

[Trait("Speed", "Slow")]
public class WritePerformanceBenchmarkTests
{
    [Fact]
    public async Task WriteAsync_PerformanceMeasurement_BaselineVsZeroShotFiltering()
    {
        // Arrange: Setup test data root
        var dataRoot = Path.Combine(Path.GetTempPath(), "airaccoon_write_bench_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        try
        {
            var options = TestData.CreateInfrastructureOptions(dataRoot);
            var services = new ServiceCollection();
            services.RegisterMemoryServices(options, new[] { McpTransport.Stdio });
            
            // Build service provider
            using var provider = services.BuildServiceProvider();
            var memoryStore = provider.GetRequiredService<IMemoryStore>();

            const string projectId = "bench-project";
            const int iterations = 50;

            // 1. Baseline Write Measurement (Valid Architectural Memory Notes)
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < iterations; i++)
            {
                var content = $"# Architectural Decision {i}\nIn memory store v4, we use zero-shot semantic noise filtering with cosine distance.";
                await memoryStore.WriteAsync(new MemoryWriteRequest(projectId, content), TestContext.Current.CancellationToken);
            }

            sw.Stop();
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();

            var totalMs = sw.Elapsed.TotalMilliseconds;
            var avgMsPerWrite = totalMs / iterations;
            var opsPerSec = (iterations / totalMs) * 1000.0;
            var allocKbPerWrite = (allocAfter - allocBefore) / (1024.0 * iterations);

            // 2. Measure Noise Interception Throughput (Terminal completion logs)
            var noiseSw = Stopwatch.StartNew();
            int interceptedCount = 0;

            for (int i = 0; i < iterations; i++)
            {
                var noiseContent = $"[IMPORTANT: Background process proc_{i} completed normally (exit code 0).\nCommand: cd /tmp && dotnet test\nOutput: test]";
                var entry = await memoryStore.WriteAsync(new MemoryWriteRequest(projectId, noiseContent), TestContext.Current.CancellationToken);
                if (entry.Hash == "noise_hash")
                {
                    interceptedCount++;
                }
            }

            noiseSw.Stop();
            var noiseAvgMsPerWrite = noiseSw.Elapsed.TotalMilliseconds / iterations;

            // Functional gate: every noise log is intercepted before the store. Latency and
            // throughput are measured for the report, not asserted — they vary by machine, so a
            // hard threshold here would only flake under CI load.
            interceptedCount.ShouldBe(iterations);

            // Write report file
            var report = $@"# Write Performance Benchmark Report (baseline -> change -> effect)

Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC
Iterations: {iterations}

## Baseline Write Performance (Valid Architectural Notes)
- Total Time: {totalMs:F2} ms
- Avg Latency per Write: {avgMsPerWrite:F3} ms
- Write Throughput: {opsPerSec:F2} ops/sec
- Allocated Memory per Write: {allocKbPerWrite:F2} KB

## Noise Interception Performance (Terminal Completion Logs)
- Total Time: {noiseSw.Elapsed.TotalMilliseconds:F2} ms
- Avg Latency per Noise Intercept: {noiseAvgMsPerWrite:F3} ms
- Rejection Accuracy: {interceptedCount}/{iterations} ({interceptedCount * 100.0 / iterations}%)
";

            var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
            var docsDir = Path.Combine(repoRoot, "docs", "work");
            Directory.CreateDirectory(docsDir);
            var reportPath = Path.Combine(docsDir, "2026-08-13-v4-write-performance-benchmark-report.md");
            await File.WriteAllTextAsync(reportPath, report, TestContext.Current.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                try { Directory.Delete(dataRoot, true); } catch { }
            }
        }
    }

    private static string FindRepoRoot(string startDir)
    {
        var current = new DirectoryInfo(startDir);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "pyproject.toml")) ||
                File.Exists(Path.Combine(current.FullName, "AiRaccoon.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
