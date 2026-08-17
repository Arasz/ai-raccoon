using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Release-version contract: VERSION at the repo root is the one hand-written version marker;
///     the built assembly and server.json must derive from it, with no literal duplicate.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class VersionContractTests
{
    private static string ReadVersionFile() => File.ReadAllText(TestData.RepoFile("VERSION")).Trim();

    [Fact]
    public void VersionFile_IsABareSemverWithNoPrereleaseSuffix()
    {
        ReadVersionFile().ShouldMatch(@"^\d+\.\d+\.\d+$");
    }

    [Fact]
    public void BuiltAssembly_AssemblyVersion_DerivesFromTheVersionFile()
    {
        var expected = Version.Parse($"{ReadVersionFile()}.0");

        typeof(MemoryTools).Assembly.GetName().Version.ShouldBe(expected);
    }

    [Fact]
    public void BuiltAssembly_InformationalVersion_DerivesFromTheVersionFile()
    {
        var expected = ReadVersionFile();
        var informational = typeof(MemoryTools).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // Once R2 lands, the SDK appends "+<sha>"; only the part before it is the version.
        informational.Split('+')[0].ShouldBe(expected);
    }

    [Fact]
    public async Task PackageVersion_ResolvesToTheVersionFile_ThroughMsBuildEvaluation()
    {
        var expected = ReadVersionFile();
        var csproj = TestData.RepoFile("src/AiRaccoon/AiRaccoon.csproj");

        var run = await RaccoonProcess.RunAsync(
            "dotnet",
            ["build", csproj, "-getProperty:PackageVersion", "--nologo"],
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken);

        run.ExitCode.ShouldBe(0, run.Stderr);
        run.Stdout.Trim().ShouldBe(expected);
    }

    /// <summary>The single-marker gate: the tracked file must hold the substitution token, never a real version.</summary>
    [Fact]
    public void TrackedMcpServerJson_HoldsNoLiteralVersion()
    {
        var text = File.ReadAllText(TestData.RepoFile("src/AiRaccoon/.mcp/server.json"));

        Regex.IsMatch(text, @"\d+\.\d+\.\d+").ShouldBeFalse(
            "src/AiRaccoon/.mcp/server.json must hold no literal semver — both version slots derive " +
            "from VERSION at pack time via the __VERSION__ token.");
    }

    /// <summary>Answers R1's objection (docs/plans/2026-08-15-performance-metrics-implementation.md §R1): a
    /// manifest correct in the repo but wrong in the package is invisible unless the gate opens the
    /// actual .nupkg, so this reads the packed file rather than the obj/ intermediate copy.</summary>
    [Fact]
    public async Task PackedMcpServerJson_CarriesTheVersionFileVersion()
    {
        var expected = ReadVersionFile();
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

    [Fact]
    public void PackageId_MatchesServerIdentifier_CommandUnchanged()
    {
        var csproj = XDocument.Load(TestData.RepoFile("src/AiRaccoon/AiRaccoon.csproj"));
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.RepoFile("src/AiRaccoon/.mcp/server.json")));
        var root = doc.RootElement;

        string Property(string name) => csproj.Descendants("PropertyGroup").Elements(name).First().Value;

        Property("PackageId").ShouldBe("ai-raccoon");
        root.GetProperty("packages")[0].GetProperty("identifier").GetString().ShouldBe("ai-raccoon");
        Property("ToolCommandName").ShouldBe("ai-raccoon");
    }

    [Fact]
    public void McpServerJson_ConformsToRegistrySchemaConstraints()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.RepoFile("src/AiRaccoon/.mcp/server.json")));
        var root = doc.RootElement;

        root.GetProperty("description").GetString()!.Length.ShouldBeLessThanOrEqualTo(100);
        var envVars = root.GetProperty("packages")[0].GetProperty("environmentVariables");
        envVars.GetArrayLength().ShouldBeGreaterThan(0);
        foreach (var envVar in envVars.EnumerateArray())
        {
            envVar.ValueKind.ShouldBe(JsonValueKind.Object);
            envVar.GetProperty("name").GetString().ShouldNotBeNullOrEmpty();
        }

        root.GetProperty("repository").GetProperty("url").GetString().ShouldBe("https://github.com/Arasz/ai-raccoon");
    }
}
