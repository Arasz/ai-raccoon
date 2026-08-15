using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Combines the tiered guard's verdict with the always-on length verdict into the one
///     caller-visible warning string memory_search returns (docs/adr/0040, docs/adr/0071). Pulled out
///     of MemoryTools so the tool method calls one pure function instead of holding the combining
///     logic itself (ToolMethodSizeTests: a tool composes services, it does not hold logic).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SearchWarningsTests
{
    [Fact]
    public void Compose_WithBothClean_ReturnsNull()
    {
        SearchWarnings.Compose(QueryGuardVerdict.Clean, QueryGuardVerdict.Clean).ShouldBeNull();
    }

    [Fact]
    public void Compose_WithOnlyTheGuardWarning_ReturnsItsGuidance()
    {
        var guard = QueryGuardVerdict.Warn("p", "guard says X");

        SearchWarnings.Compose(guard, QueryGuardVerdict.Clean).ShouldBe("guard says X");
    }

    [Fact]
    public void Compose_WithOnlyTheLengthWarning_ReturnsItsGuidance()
    {
        var length = QueryGuardVerdict.Warn("p", "length says Y");

        SearchWarnings.Compose(QueryGuardVerdict.Clean, length).ShouldBe("length says Y");
    }

    [Fact]
    public void Compose_WithBothWarnings_ReturnsBothGuidanceTexts()
    {
        var guard = QueryGuardVerdict.Warn("p1", "guard says X");
        var length = QueryGuardVerdict.Warn("p2", "length says Y");

        var combined = SearchWarnings.Compose(guard, length);

        combined.ShouldNotBeNullOrWhiteSpace();
        combined!.ShouldContain("guard says X");
        combined.ShouldContain("length says Y");
    }

    [Fact]
    public void Compose_IgnoresARefuseVerdict_NeverCalledWithOneInPractice()
    {
        // Refuse throws before the caller ever composes a warning (MemoryTools.Search); Compose
        // itself only treats Warn as warning-worthy, so a stray Refuse is silently not a warning
        // rather than a crash -- documented here so that choice is not accidental.
        var refuse = QueryGuardVerdict.Refuse("p", "refuse guidance");

        SearchWarnings.Compose(refuse, QueryGuardVerdict.Clean).ShouldBeNull();
    }
}
