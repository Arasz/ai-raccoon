using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>A name lookup answers with exactly one id, or refuses: none is not-found, several is ambiguous.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectNameMatchTests
{
    [Fact]
    public void Single_One_ReturnsIt()
    {
        ProjectNameMatch.Single("ai-badger", ["ai-badger"]).ShouldBe("ai-badger");
    }

    [Fact]
    public void Single_None_ThrowsNotFound()
    {
        var refusal = Should.Throw<ProjectNotFoundException>(() => ProjectNameMatch.Single("acme", []));

        refusal.Message.ShouldContain("'acme'");
        refusal.Message.ShouldContain("project_id_token_get");
    }

    [Fact]
    public void Single_Two_ThrowsAmbiguousListingBoth()
    {
        const string first = "0199a1b2-0000-7000-8000-000000000001";
        const string second = "0199a1b2-0000-7000-8000-000000000002";

        var refusal = Should.Throw<ProjectNameAmbiguousException>(() => ProjectNameMatch.Single("acme", [first, second]));

        refusal.Message.ShouldContain("'acme'");
        refusal.Message.ShouldContain(first);
        refusal.Message.ShouldContain(second);
        refusal.ProjectIds.ShouldBe([first, second]);
    }
}
