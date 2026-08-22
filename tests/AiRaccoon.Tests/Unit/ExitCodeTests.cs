using System.Reflection;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit;

/// <summary>Process exit codes of the ai-raccoon CLI.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ExitCodeTests
{
    [Fact]
    public void PortInUse_IsThree()
    {
        ExitCode.PortInUse.ShouldBe(3);
    }

    /// <summary>Delta review C3: distinguishes "no bank at the resolved path" from HEALTHY (0).</summary>
    [Fact]
    public void NoBank_IsTwentyTwo()
    {
        ExitCode.NoBank.ShouldBe(22);
    }

    /// <summary>One code, one reason: a value shared by two names is a code that cannot be acted on.</summary>
    [Fact]
    public void EveryExitCode_IsDistinct()
    {
        var codes = typeof(ExitCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!);

        var duplicates = codes.GroupBy(pair => pair.Value)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} = {string.Join(", ", group.Select(pair => pair.Key))}")
            .ToList();

        duplicates.ShouldBeEmpty("these exit codes share a value: " + string.Join("; ", duplicates));
        codes.Count.ShouldBeGreaterThan(10, "the guard must actually see the codes");
    }

    /// <summary>8 was RestartFailed and was retired on the split; reusing it would silently change meaning.</summary>
    [Fact]
    public void EightStaysRetired()
    {
        typeof(ExitCode)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(int))
            .Select(f => (int)f.GetRawConstantValue()!)
            .ShouldNotContain(8);
    }
}
