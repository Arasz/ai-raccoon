using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering.Policies;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class PromotionScorerTtlPolicyTests
{
    private readonly PromotionScorerTtlPolicy _sut = new();

    [Fact]
    public void EvaluateTtl_WithEmptyContent_AssignsTransientTtl()
    {
        // Arrange
        var request = new MemoryWriteRequest("proj-1", "   ");

        // Act
        var ttl = _sut.EvaluateTtl(request);

        // Assert
        Assert.Equal(3, ttl);
    }

    [Fact]
    public void EvaluateTtl_WithSubstantialContent_ReturnsNullForPermanentStorage()
    {
        // Arrange
        var content = "This is a highly detailed architectural decision record explaining how the SQLite memory store works and why we chose FTS5 for the search engine.";
        var request = new MemoryWriteRequest("proj-1", content, null, null, null, "docs/adr-001.md");

        // Act
        var ttl = _sut.EvaluateTtl(request);

        // Assert
        Assert.Null(ttl); // Good content gets no TTL override (stays permanently)
    }

    [Fact]
    public void EvaluateTtl_WithShortTransientContent_AssignsTransientTtl()
    {
        // Arrange
        var content = "test run failed";
        var request = new MemoryWriteRequest("proj-1", content);

        // Act
        var ttl = _sut.EvaluateTtl(request);

        // Assert
        Assert.Equal(3, ttl); // Short fragments get caught by the scorer's "thin-cap" or low word count logic
    }
}
