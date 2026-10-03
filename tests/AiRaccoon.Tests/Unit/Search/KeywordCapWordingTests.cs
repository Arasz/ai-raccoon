using System.ComponentModel;
using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     Caller-facing text about the keyword leg must match ADR-0126: a long query's keyword terms are capped,
///     so no string may tell a caller that keyword matching sees the whole query.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class KeywordCapWordingTests
{
    [Fact]
    public void CodeTrimWarning_NamesTheKeywordCap()
    {
        // This warning is appended to the memory leg's length warning, so a "still saw it in full" here
        // contradicts the capped sentence that precedes it in the same response.
        var warning = CodeSearchWarnings.QueryTrimmedToCodeWindow;

        warning.ShouldContain($"first {SearchDefaults.MaxKeywordTerms} distinct words");
        warning.ShouldNotContain("in full");
    }

    [Fact]
    public void SearchQueryDescription_NamesTheKeywordCap()
    {
        var description = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), BindingFlags.Public | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetParameters()
            .Single(p => p.Name == "query")
            .GetCustomAttribute<DescriptionAttribute>()
            .ShouldNotBeNull()
            .Description;

        description.ShouldContain($"first {SearchDefaults.MaxKeywordTerms} distinct words");
        description.ShouldNotContain("Keyword matching still covers the query in full");
    }
}
