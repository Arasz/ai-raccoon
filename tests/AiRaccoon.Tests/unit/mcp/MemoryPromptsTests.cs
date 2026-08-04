using AiRaccoon.Prompts;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Prompts;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MemoryPromptsTests
{
    private readonly MemoryPrompts _prompts = new();

    [Fact]
    public void MemoryUsageGuide_WithoutProjectId_StatesProjectIdIsRequired()
    {
        var guide = _prompts.MemoryUsageGuide();

        guide.ShouldContain("project_id");
        guide.ShouldContain("workspace");
        guide.ShouldContain("memory_share");
        guide.ShouldContain("scope=all");
    }

    [Fact]
    public void MemoryUsageGuide_WithProjectId_NamesTheProject()
    {
        var guide = _prompts.MemoryUsageGuide("acme");

        guide.ShouldContain("acme");
        guide.ShouldContain("project_id=acme");
    }

    [Fact]
    public void WorkspaceConsolidationGuide_ListsTheRitualSteps()
    {
        var guide = _prompts.WorkspaceConsolidationGuide("ws-1", "acme");

        guide.ShouldContain("ws-1");
        guide.ShouldContain("memory_workspace_status");
        guide.ShouldContain("memory_workspace_consolidate");
        guide.ShouldContain("memory_share");
    }
}
