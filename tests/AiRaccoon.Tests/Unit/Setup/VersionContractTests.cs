using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Release-version contract: the tool package and its MCP registry metadata must
///     report the same stable version with no prerelease suffix.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class VersionContractTests
{
    private const string ExpectedVersion = "1.6.0";

    [Fact]
    public void PackageMetadata_IsStable_OnePointZero()
    {
        var csproj = XDocument.Load(TestData.RepoFile("src/AiRaccoon/AiRaccoon.csproj"));

        string Property(string name) => csproj.Descendants("PropertyGroup").Elements(name).First().Value;

        Property("PackageVersion").ShouldBe(ExpectedVersion);
        Property("InformationalVersion").ShouldBe(ExpectedVersion);
        Property("AssemblyVersion").ShouldBe(ExpectedVersion);
    }

    [Fact]
    public void McpServerJson_Versions_MatchPackageVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.RepoFile("src/AiRaccoon/.mcp/server.json")));
        var root = doc.RootElement;

        root.GetProperty("version").GetString().ShouldBe(ExpectedVersion);
        root.GetProperty("packages")[0].GetProperty("version").GetString().ShouldBe(ExpectedVersion);
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

    [Fact]
    public void DeclaredVersions_CarryNoPrereleaseSuffix()
    {
        var csproj = XDocument.Load(TestData.RepoFile("src/AiRaccoon/AiRaccoon.csproj"));
        var versions = csproj.Descendants("PropertyGroup")
            .Elements("PackageVersion")
            .Concat(csproj.Descendants("PropertyGroup").Elements("InformationalVersion"))
            .Concat(csproj.Descendants("PropertyGroup").Elements("AssemblyVersion"))
            .Select(e => e.Value)
            .ToArray();

        versions.ShouldNotBeEmpty();
        foreach (var version in versions)
        {
            version.Contains('-').ShouldBeFalse($"prerelease suffix not allowed: {version}");
        }
    }

}
