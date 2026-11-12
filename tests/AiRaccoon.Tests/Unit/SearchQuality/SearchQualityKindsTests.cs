using AiRaccoon.Core.Memory;
using AiRaccoon.Core.SearchQuality;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.SearchQuality;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchQualityKindsTests
{
    public static TheoryData<EntryType> EveryNonEmptySelection()
    {
        var data = new TheoryData<EntryType>();
        for (var bits = 1; bits <= (int)EntryTypeParameter.Default; bits++)
        {
            data.Add((EntryType)bits);
        }

        return data;
    }

    /// <summary>
    ///     The legacy kind must cover the selection it labels: a Memory+Code query labelled
    ///     "memory" would record a mask that drops Code. Mutation: map Memory|Code to "memory".
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryNonEmptySelection))]
    public void MaskOf_KindOf_CoversTheSelection(EntryType selection)
    {
        var mask = SearchQualityKinds.MaskOf(SearchQualityKinds.KindOf(selection));

        mask.ShouldNotBeNull();
        (mask.Value & selection).ShouldBe(selection);
    }
}
