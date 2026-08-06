using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchWindowTests
{
    [Theory]
    [InlineData(1, 100)]
    [InlineData(20, 100)]
    [InlineData(40, 120)]
    [InlineData(100, 300)]
    public void CandidateWindowFor_IsMaxOfThreeTimesLimitAndOneHundred(int limit, int expected) => SqliteMemoryStore.CandidateWindowFor(limit).ShouldBe(expected);

    [Fact]
    public void CandidateWindowFor_ClampsToIntMax_WithoutOverflow() => SqliteMemoryStore.CandidateWindowFor(int.MaxValue).ShouldBe(int.MaxValue);

    [Theory]
    [InlineData(1, 50)]
    [InlineData(10, 50)]
    [InlineData(20, 100)]
    [InlineData(40, 200)]
    public void CandidateWindowFor_Max5x50Window_IsMaxOfFiveTimesLimitAndFifty(int limit, int expected) => SqliteMemoryStore.CandidateWindowFor(limit, CandidateWindowMode.Max5X50).ShouldBe(expected);
}
