using Shouldly;
using Xunit;

namespace AiRaccon.Tests;

public class RandomNumberToolsTests
{
    private readonly RandomNumberTools _tools = new();

    [Fact]
    public void GetRandomNumber_WithDefaultRange_ReturnsValueWithinBounds()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var result = _tools.GetRandomNumber();
            result.ShouldBeGreaterThanOrEqualTo(0);
            result.ShouldBeLessThan(100);
        }
    }

    [Fact]
    public void GetRandomNumber_WithCustomRange_ReturnsValueWithinBounds()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var result = _tools.GetRandomNumber(5, 10);
            result.ShouldBeGreaterThanOrEqualTo(5);
            result.ShouldBeLessThan(10);
        }
    }

    [Fact]
    public void GetRandomNumber_WithNegativeRange_ReturnsValueWithinBounds()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var result = _tools.GetRandomNumber(-10, -5);
            result.ShouldBeGreaterThanOrEqualTo(-10);
            result.ShouldBeLessThan(-5);
        }
    }

    [Fact]
    public void GetRandomNumber_WhenMinEqualsMax_ReturnsThatValue()
    {
        var result = _tools.GetRandomNumber(7, 7);
        result.ShouldBe(7);
    }

    [Fact]
    public void GetRandomNumber_WhenRangeHasSingleValue_ReturnsMin()
    {
        for (var i = 0; i < 1_000; i++)
        {
            _tools.GetRandomNumber(5, 6).ShouldBe(5);
        }
    }

    [Fact]
    public void GetRandomNumber_WhenMinGreaterThanMax_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => _tools.GetRandomNumber(10, 5));
    }

    [Fact]
    public void GetRandomNumber_OverManyDraws_ProducesMoreThanOneValue()
    {
        // Guards against a degenerate implementation that always returns the
        // same value (e.g. always min): every range test above would pass.
        var seen = new HashSet<int>();
        for (var i = 0; i < 1_000; i++)
        {
            seen.Add(_tools.GetRandomNumber(0, 100));
        }

        seen.Count.ShouldBeGreaterThan(1);
    }
}
