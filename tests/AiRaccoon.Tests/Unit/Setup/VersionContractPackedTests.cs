using System.IO.Compression;
using System.Text.Json;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     The pack-and-inspect version contract (diagnose-fast-tests): opens the ACTUAL .nupkg, so it
///     runs a full `dotnet pack` — heavyweight and host-crash-prone on loaded runners, kept out of
///     the 15-minute fast lane (build-slow still gates it on every push and carries crash dumps).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public class VersionContractPackedTests
{
    /// <summary>Answers R1's objection (docs/plans/2026-08-15-performance-metrics-implementation.md §R1): a
    /// manifest correct in the repo but wrong in the package is invisible unless the gate opens the
    /// actual .nupkg, so this reads the packed file rather than the obj/ intermediate copy.</summary>
    [RetryFact]
    public async Task PackedMcpServerJson_CarriesTheVersionFileVersion()
    {
        var expected = File.ReadAllText(TestData.RepoFile("VERSION")).Trim();
        var csproj = TestData.RepoFile("src/AiRaccoon/AiRaccoon.csproj");
        var outDir = TestData.CreateTempRoot("ai-raccoon-pack");

        try
        {
            var run = await RaccoonProcess.RunAsync(
                "dotnet",
                ["pack", csproj, "-o", outDir, "--nologo"],
                TimeSpan.FromMinutes(3),
                TestContext.Current.CancellationToken);

            run.ExitCode.ShouldBe(0, run.Stderr);

            // "dotnet pack" also emits per-RID packages (ai-raccoon.<rid>.<version>.nupkg) because the
            // csproj declares <RuntimeIdentifiers>; the non-RID one is the package registries resolve.
            var nupkg = Path.Combine(outDir, $"ai-raccoon.{expected}.nupkg");
            File.Exists(nupkg).ShouldBeTrue(
                $"expected {nupkg} after packing. Found: {string.Join(", ", Directory.GetFiles(outDir))}");

            using var zip = ZipFile.OpenRead(nupkg);
            var entry = zip.GetEntry(".mcp/server.json");
            entry.ShouldNotBeNull("the packed nupkg has no .mcp/server.json entry");

            await using var stream = entry.Open();
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
            var root = doc.RootElement;

            root.GetProperty("version").GetString().ShouldBe(expected);
            root.GetProperty("packages")[0].GetProperty("version").GetString().ShouldBe(expected);

            zip.Entries.Select(e => e.FullName).ShouldNotContain(e => e.Contains("onnxruntime", StringComparison.Ordinal),
                "the RID-agnostic nupkg must not carry an ONNX Runtime core");

            AssertWebGpuCorePlacement(outDir, expected);
        }
        finally
        {
            TestData.DeleteTempRoot(outDir);
        }
    }

    /// <summary>
    ///     ADR-0115: the win-x64, win-arm64 and linux-x64 packages carry the fetched WebGPU core in place of
    ///     the NuGet core (same size, byte for byte where it lands), plus Dawn's DXC beside it on Windows;
    ///     the other RIDs keep the NuGet core and never carry the retired plugin. Checked in the same pack run.
    /// </summary>
    private static void AssertWebGpuCorePlacement(string outDir, string version)
    {
        Dictionary<string, long> Entries(string rid)
        {
            var nupkg = Path.Combine(outDir, $"ai-raccoon.{rid}.{version}.nupkg");
            File.Exists(nupkg).ShouldBeTrue($"expected {nupkg}. Found: {string.Join(", ", Directory.GetFiles(outDir))}");
            using var zip = ZipFile.OpenRead(nupkg);
            return zip.Entries.ToDictionary(e => e.FullName, e => e.Length);
        }

        long FetchedLength(string rid, string file) =>
            new FileInfo(TestData.RepoFile($"src/AiRaccoon/webgpu-core/{rid}/{file}")).Length;

        foreach (var (rid, files) in new[]
                 {
                     ("win-x64", new[] { "onnxruntime.dll", "dxcompiler.dll", "dxil.dll" }),
                     ("win-arm64", new[] { "onnxruntime.dll", "dxcompiler.dll", "dxil.dll" }),
                     ("linux-x64", new[] { "libonnxruntime.so" }),
                 })
        {
            var entries = Entries(rid);
            foreach (var file in files)
            {
                var path = $"tools/net10.0/{rid}/{file}";
                entries.ShouldContainKey(path, $"{rid} package lacks {file}");
                entries[path].ShouldBe(FetchedLength(rid, file), $"{rid} package's {file} is not the fetched WebGPU core's");
            }
        }

        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "osx-arm64" })
        {
            if (!File.Exists(Path.Combine(outDir, $"ai-raccoon.{rid}.{version}.nupkg")))
            {
                continue;
            }

            Entries(rid).Keys.ShouldNotContain(e => e.Contains("providers_webgpu", StringComparison.Ordinal),
                $"{rid} package must not carry the retired WebGPU plugin");
        }
    }
}
