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
}
