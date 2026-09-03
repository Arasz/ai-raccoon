using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     Single-source pin for the search-kind vocabulary: the MCP <c>kind</c> param
///     (MemoryTools.ParseKind), the stored <c>search_quality.kind</c> value (SearchDispatcher),
///     and the column CHECK (<c>CHECK(kind IN ('memory','code','both'))</c>) must agree.
///     A rename must fail this build, never a CHECK at runtime inside fire-and-forget
///     quality recording (which swallows and logs).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchKindWireNamesTests
{
    [Fact]
    public void WireNames_AreTheStableContract()
    {
        SearchKindWireNames.Memory.ShouldBe("memory");
        SearchKindWireNames.Code.ShouldBe("code");
        SearchKindWireNames.Both.ShouldBe("both");
    }

    [Fact]
    public void ToWireString_MapsEveryEnumMember()
    {
        SearchKind.Memory.ToWireString().ShouldBe("memory");
        SearchKind.Code.ToWireString().ShouldBe("code");
        SearchKind.Both.ToWireString().ShouldBe("both");
    }
}
