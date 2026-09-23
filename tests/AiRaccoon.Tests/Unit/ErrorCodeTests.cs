using System.Reflection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>
///     The shape of the exit-code catalog (ADR-0107), read off <see cref="ErrorCode" /> by reflection:
///     two digits whose tens digit names the category, one meaning per value, at most ten cases per category.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ErrorCodeTests
{
    /// <summary>The one place a category class is tied to its tens digit.</summary>
    private static readonly Dictionary<string, int> CategoryDigits = new(StringComparer.Ordinal)
    {
        [nameof(ErrorCode.Usage)] = 1,
        [nameof(ErrorCode.Key)] = 2,
        [nameof(ErrorCode.Bank)] = 3,
        [nameof(ErrorCode.Port)] = 4,
        [nameof(ErrorCode.Server)] = 5,
        [nameof(ErrorCode.Reach)] = 6,
        [nameof(ErrorCode.Model)] = 7,
        [nameof(ErrorCode.Environment)] = 8,
        [nameof(ErrorCode.Internal)] = 9
    };

    [Fact]
    public void EveryCategoryClass_HasADigit_AndEveryDigitHasAClass()
    {
        Categories().Select(category => category.Name).Where(name => name != nameof(ErrorCode.Ok))
            .ShouldBe(CategoryDigits.Keys, ignoreOrder: true);
    }

    [Fact]
    public void Ok_HoldsExactlySuccessAndSigc()
    {
        Constants(typeof(ErrorCode.Ok)).Values.ShouldBe([0, 130], ignoreOrder: true);
        ErrorCode.Ok.Success.ShouldBe(0);
        ErrorCode.Ok.SIGC.ShouldBe(130);
    }

    [Fact]
    public void EveryFailureCode_IsTwoDigits()
    {
        var outOfRange = FailureCodes().Where(code => code.Value is < 10 or > 99)
            .Select(code => $"{code.Name} = {code.Value}").ToList();

        outOfRange.ShouldBeEmpty("a failure code is 10-99: " + string.Join("; ", outOfRange));
    }

    [Fact]
    public void EveryFailureCode_TensDigit_IsItsCategoryDigit()
    {
        var misplaced = FailureCodes().Where(code => code.Value / 10 != CategoryDigits[code.Category])
            .Select(code => $"{code.Name} = {code.Value} (category digit {CategoryDigits[code.Category]})").ToList();

        misplaced.ShouldBeEmpty("these codes sit in another category's decade: " + string.Join("; ", misplaced));
    }

    [Fact]
    public void EveryCode_IsDistinct()
    {
        var all = Categories().SelectMany(category =>
            Constants(category).Select(pair => (Name: $"{category.Name}.{pair.Key}", pair.Value))).ToList();

        var duplicates = all.GroupBy(code => code.Value).Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} = {string.Join(", ", group.Select(code => code.Name))}").ToList();

        duplicates.ShouldBeEmpty("these codes share a value: " + string.Join("; ", duplicates));
        all.Count.ShouldBeGreaterThan(CategoryDigits.Count, "the guard must actually see the codes");
    }

    [Fact]
    public void NoCategory_HasMoreThanTenCases()
    {
        var crowded = Categories().Where(category => Constants(category).Count > 10)
            .Select(category => $"{category.Name} ({Constants(category).Count})").ToList();

        crowded.ShouldBeEmpty("a category has ten ones digits: " + string.Join("; ", crowded));
    }

    [Fact]
    public void EveryCategory_HasItsGeneralCase()
    {
        var missing = CategoryDigits.Where(pair => !FailureCodes().Any(code => code.Category == pair.Key && code.Value == pair.Value * 10))
            .Select(pair => pair.Key).ToList();

        missing.ShouldBeEmpty("x0 is each category's most general case: " + string.Join(", ", missing));
    }

    private static IEnumerable<Type> Categories() =>
        typeof(ErrorCode).GetNestedTypes(BindingFlags.Public).Where(type => type is { IsAbstract: true, IsSealed: true });

    private static Dictionary<string, int> Constants(Type category) =>
        category.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(int))
            .ToDictionary(field => field.Name, field => (int)field.GetRawConstantValue()!);

    private static IEnumerable<(string Category, string Name, int Value)> FailureCodes() =>
        Categories().Where(category => category.Name != nameof(ErrorCode.Ok))
            .SelectMany(category => Constants(category).Select(pair => (category.Name, $"{category.Name}.{pair.Key}", pair.Value)));
}
