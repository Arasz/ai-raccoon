using System.Reflection;
using System.Text.Json;
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

    [Fact]
    public void McpServerJson_Versions_MatchTheBuiltAssemblyVersion()
    {
        var informational = typeof(MemoryTools).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.RepoFile("src/AiRaccoon/.mcp/server.json")));
        var root = doc.RootElement;

        root.GetProperty("version").GetString().ShouldBe(informational);
        root.GetProperty("packages")[0].GetProperty("version").GetString().ShouldBe(informational);
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
