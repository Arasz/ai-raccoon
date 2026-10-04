using AiRaccoon.Core.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>A scope write that only removes paths from a stored list is a subset; anything else is not.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class IngestScopeListTests
{
    [Theory]
    [InlineData("""["/a","/b"]""", """["/a"]""")]
    [InlineData("""["/a","/b"]""", """["/b","/a"]""")]
    [InlineData("""["/a"]""", "[]")]
    public void IsSubset_ARemovalOrTheSameList_IsTrue(string stored, string proposed)
    {
        IngestScopeList.IsSubset(stored, proposed).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""["/a"]""", """["/a","/b"]""")]
    [InlineData("""["/a"]""", """["/b"]""")]
    [InlineData("""["/a"]""", """["/A"]""")]
    public void IsSubset_AnAddedPath_IsFalse(string stored, string proposed)
    {
        IngestScopeList.IsSubset(stored, proposed).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, "[]")]
    [InlineData("", "[]")]
    [InlineData("""["/a"]""", "not json")]
    [InlineData("""["/a"]""", "null")]
    [InlineData("not json", "[]")]
    public void IsSubset_NothingStoredOrAnUnreadableValue_IsFalse(string? stored, string proposed)
    {
        IngestScopeList.IsSubset(stored, proposed).ShouldBeFalse();
    }
}
