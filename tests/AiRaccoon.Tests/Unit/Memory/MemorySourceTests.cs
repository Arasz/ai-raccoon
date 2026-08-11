using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySourceTests
{
    [Fact]
    public void MemorySource_Equality_SameLocatorSameType_AreEqual()
    {
        var a = new MemorySource { Id = 1, SourceType = SourceType.File, SourceLocator = "docs/readme.md", Section = "## Intro" };
        var b = new MemorySource { Id = 2, SourceType = SourceType.File, SourceLocator = "docs/readme.md", Section = "## Intro" };

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void MemorySource_Equality_DifferentType_AreNotEqual()
    {
        var a = new MemorySource { Id = 1, SourceType = SourceType.File, SourceLocator = "docs/readme.md", Section = "## Intro" };
        var b = new MemorySource { Id = 1, SourceType = SourceType.Transcript, SourceLocator = "docs/readme.md", Section = "## Intro" };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void SourceLocator_Empty_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            new MemorySource { Id = 1, SourceType = SourceType.File, SourceLocator = "" });
    }

    [Fact]
    public void SourceType_Invalid_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            new MemorySource { Id = 1, SourceType = (SourceType)99, SourceLocator = "x" });
    }
}
