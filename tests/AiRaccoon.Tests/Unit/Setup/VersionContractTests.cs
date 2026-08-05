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
    private const string ExpectedVersion = "1.0.1";

    [Fact]
    public void PackageMetadata_IsStable_OnePointZero()
    {
        var csproj = XDocument.Load(RepoFile("src/AiRaccoon/AiRaccoon.csproj"));

        string Property(string name) => csproj.Descendants("PropertyGroup").Elements(name).First().Value;

        Property("PackageVersion").ShouldBe(ExpectedVersion);
        Property("InformationalVersion").ShouldBe(ExpectedVersion);
        Property("AssemblyVersion").ShouldBe(ExpectedVersion);
    }

    [Fact]
    public void McpServerJson_Versions_MatchPackageVersion()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("src/AiRaccoon/.mcp/server.json")));
        var root = doc.RootElement;

        root.GetProperty("version").GetString().ShouldBe(ExpectedVersion);
        root.GetProperty("packages")[0].GetProperty("version").GetString().ShouldBe(ExpectedVersion);
    }

    [Fact]
    public void DeclaredVersions_CarryNoPrereleaseSuffix()
    {
        var csproj = XDocument.Load(RepoFile("src/AiRaccoon/AiRaccoon.csproj"));
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

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {relative} from the test output directory.");
    }
}
