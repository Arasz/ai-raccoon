using System.ComponentModel;
using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Docs;

/// <summary>
///     F6/F50: a fresh install has no memory embedding engine, and until this package landed every
///     first-run surface either pitched hybrid memory as the default or prescribed only the code
///     corpus's remedy. These parse the shipped surfaces themselves — the README's Quick Start, the
///     tutorial's verification step, the how-to's opening, and the initialize instructions every
///     agent receives — so the activation step cannot drift out of them silently.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FirstRunMemoryEngineTests
{
    [Fact]
    public void ReadmeQuickStart_ActivatesTheMemoryEngine()
    {
        var quickStart = Section(File.ReadAllText(TestData.RepoFile("README.md")), "## Quick Start");

        quickStart.ShouldContain(EmbeddingEngineSetup.DefaultModelCommand,
            customMessage: "a fresh bank is keyword-only until this runs; Quick Start is where a reader acts");
    }

    [Fact]
    public void Tutorial_ActivatesTheMemoryEngine()
    {
        var tutorial = File.ReadAllText(TestData.RepoFile("docs/tutorials/get-started-with-ai-raccoon.md"));

        tutorial.ShouldContain(EmbeddingEngineSetup.DefaultModelCommand,
            customMessage: "the first-run walkthrough must activate the memory engine it later verifies");
    }

    /// <summary>F50: Step 4's call is the install-verification criterion, and it must parse as a real call.</summary>
    [Fact]
    public void TutorialStep4_NamesTheRequiredSessionId()
    {
        var step4 = Section(File.ReadAllText(TestData.RepoFile("docs/tutorials/get-started-with-ai-raccoon.md")), "## Step 4");

        step4.ShouldContain("sessionId",
            customMessage: "sessionId became required in 1.38.0; the tutorial's verification call fails without it");
    }

    [Fact]
    public void HowTo_OpensWithTheFreshBankState()
    {
        var howTo = File.ReadAllText(TestData.RepoFile("docs/how-to/configure-embedding-engines.md"));

        howTo.ShouldContain("A fresh bank has no memory engine",
            customMessage: "the how-to must not let 'Local (Default)' read as already active");
        howTo.ShouldContain(EmbeddingEngineSetup.DefaultModelCommand);
    }

    [Fact]
    public void McpServerInstructions_NameBothEngineRemedies()
    {
        McpServerInstructions.Text.ShouldContain(SearchWarnings.EngineNotConfiguredPrefix,
            customMessage: "an agent that cannot recognise the memory warning cannot relay it");
        McpServerInstructions.Text.ShouldContain(EmbeddingEngineSetup.DefaultModelCommand);
        McpServerInstructions.Text.ShouldContain(CodeSearchWarnings.EngineNotConfiguredPrefix);
        McpServerInstructions.Text.ShouldContain(CodeEngineSetup.DefaultModelCommand);
    }

    [Fact]
    public void MemorySearchToolDescription_NamesTheMemoryRemedy()
    {
        var description = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        description.ShouldContain(SearchWarnings.EngineNotConfiguredPrefix,
            customMessage: "the tool description is the only guidance a client that ignores server instructions sees");
        description.ShouldContain(EmbeddingEngineSetup.DefaultModelCommand);
        description.ShouldContain(CodeEngineSetup.DefaultModelCommand,
            customMessage: "the code remedy must survive the memory one being added");
    }

    /// <summary>The section from <paramref name="heading" /> to the next level-2 heading, headings excluded.</summary>
    private static string Section(string document, string heading)
    {
        var start = document.IndexOf(heading, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"'{heading}' not found in the document");

        var rest = document[start..];
        var end = rest.IndexOf("\n## ", 1, StringComparison.Ordinal);
        var section = end < 0 ? rest : rest[..end];
        section.ShouldNotBe(heading, $"'{heading}' is the last thing before another heading; the parse found nothing");
        return section;
    }
}
