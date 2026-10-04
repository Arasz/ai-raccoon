using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>The one project-name rule the /projects endpoint and the CLI register verb share.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectNameTests
{
    [Fact]
    public void Null_IsUsable_TheNameIsOptional()
    {
        ProjectName.TryGetRefusal(null).ShouldBeNull();
    }

    [Fact]
    public void TwoHundredCharacters_IsUsable()
    {
        ProjectName.TryGetRefusal(new string('a', 200)).ShouldBeNull();
    }

    [Fact]
    public void TwoHundredOneCharacters_RefusesWithTheCap()
    {
        ProjectName.TryGetRefusal(new string('a', 201)).ShouldBe("must be at most 200 characters");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_Refuses(string name)
    {
        ProjectName.TryGetRefusal(name).ShouldBe("must not be blank");
    }

    [Theory]
    [InlineData("acme\u001b")]
    [InlineData("ac\rme")]
    [InlineData("ac\nme")]
    [InlineData("ac\tme")]
    public void ControlCharacters_Refuse(string name)
    {
        ProjectName.TryGetRefusal(name).ShouldBe("must not contain control characters");
    }
}
