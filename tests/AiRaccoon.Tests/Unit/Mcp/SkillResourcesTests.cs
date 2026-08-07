using System.Text.Json;
using AiRaccoon.Resources;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SkillResourcesTests
{
    private const string IndexUri = "skill://index.json";
    private const string MemorySkillUri = "skill://ai-raccoon-memory/SKILL.md";
    private const string SkillName = "ai-raccoon-memory";

    [Fact]
    public void Index_IsValidDiscoveryDocumentWithMemorySkillEntry()
    {
        using var doc = JsonDocument.Parse(SkillResources.GetIndex());

        var root = doc.RootElement;
        root.GetProperty("$schema").GetString()!.ShouldContain("agentskills.io");

        var skill = root.GetProperty("skills").EnumerateArray().ShouldHaveSingleItem();
        skill.GetProperty("name").GetString().ShouldBe(SkillName);
        skill.GetProperty("type").GetString().ShouldBe("skill-md");
        skill.GetProperty("url").GetString().ShouldBe(MemorySkillUri);
        skill.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IndexResource_IsDeclaredWithJsonMimeType()
    {
        var attribute = typeof(SkillResources).GetMethod(nameof(SkillResources.GetIndex))!
            .GetCustomAttributes(typeof(McpServerResourceAttribute), false)
            .Cast<McpServerResourceAttribute>()
            .ShouldHaveSingleItem();

        attribute.UriTemplate.ShouldBe(IndexUri);
        attribute.MimeType.ShouldBe("application/json");
    }

    [Fact]
    public void MemorySkillResource_IsDeclaredWithMarkdownMimeType()
    {
        var attribute = typeof(SkillResources).GetMethod(nameof(SkillResources.GetMemorySkillMd))!
            .GetCustomAttributes(typeof(McpServerResourceAttribute), false)
            .Cast<McpServerResourceAttribute>()
            .ShouldHaveSingleItem();

        attribute.UriTemplate.ShouldBe(MemorySkillUri);
        attribute.MimeType.ShouldBe("text/markdown");
    }

    [Fact]
    public void MemorySkill_HasFrontmatterAndCoreSections()
    {
        var skill = SkillResources.GetMemorySkillMd();

        skill.ShouldStartWith("---");
        skill.ShouldContain($"name: {SkillName}");
        skill.ShouldContain("## 2. Search-first workflow");
        skill.ShouldContain("## 4. Write discipline");
        skill.ShouldContain("## 6. Pitfalls");
    }

    [Fact]
    public void IndexDescription_MatchesSkillFrontmatterDescription()
    {
        using var doc = JsonDocument.Parse(SkillResources.GetIndex());
        var indexDescription = doc.RootElement.GetProperty("skills")[0].GetProperty("description").GetString()!;

        SkillResources.GetMemorySkillMd().ShouldContain(indexDescription);
    }
}
