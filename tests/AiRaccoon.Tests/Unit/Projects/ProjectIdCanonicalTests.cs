using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>ADR-0089 decision 2: any Guid.TryParse-accepted spelling canonicalizes to lowercase D-form; a non-guid passes through untouched.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdCanonicalTests
{
    [Theory]
    [InlineData("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3F2504E0-4F89-11D3-9A0C-0305E82C3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3f2504e04f8911d39a0c0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public void TryCanonicalize_LowercasesAndStripsBraces(string input, string expected)
    {
        var ok = ProjectId.TryCanonicalize(input, out var canonical);

        ok.ShouldBeTrue();
        canonical.ShouldBe(expected);
    }

    [Fact]
    public void TryCanonicalize_LeavesARawTextIdUntouchedAndReturnsFalse()
    {
        var ok = ProjectId.TryCanonicalize("jsaa", out var canonical);

        ok.ShouldBeFalse();
        canonical.ShouldBe("jsaa");
    }

    /// <summary>The BCL Try-pattern contract: return false, never throw. Only Canonicalize guards.</summary>
    [Fact]
    public void TryCanonicalize_OnWhitespaceInput_ReturnsFalseWithoutThrowing()
    {
        var ok = ProjectId.TryCanonicalize("   ", out var canonical);

        ok.ShouldBeFalse();
        canonical.ShouldBe("   ");
    }

    [Fact]
    public void TryCanonicalize_OnNullInput_ReturnsFalseWithoutThrowing()
    {
        var ok = ProjectId.TryCanonicalize(null!, out var canonical);

        ok.ShouldBeFalse();
        canonical.ShouldBeNull();
    }

    [Fact]
    public void Canonicalize_OfAGuidSpelling_ReturnsTheLowercaseDForm() =>
        ProjectId.Canonicalize("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}").ShouldBe("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    [Fact]
    public void Canonicalize_OfARawTextId_ReturnsItUnchanged() =>
        ProjectId.Canonicalize("jsaa").ShouldBe("jsaa");
}
