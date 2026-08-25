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
        }
        finally
        {
            TestData.DeleteTempRoot(outDir);
        }
    }
}
