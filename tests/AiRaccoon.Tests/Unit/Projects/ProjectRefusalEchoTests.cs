using System.Globalization;
using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     Every refusal that echoes a caller-supplied id or name renders it through
///     <see cref="ProjectIdText" />, so no raw control character reaches stderr or an MCP refusal.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectRefusalEchoTests
{
    [Fact]
    public void Retired_IsEscaped()
    {
        var message = new RetiredProjectException("qa\u001bnoise").Message;

        message.ShouldContain("\\u001B");
        message.Any(IsUnsafe).ShouldBeFalse(message);
    }

    [Fact]
    public void NotFound_IsEscaped()
    {
        var message = new ProjectNotFoundException("ac\rme").Message;

        message.ShouldContain("\\u000D");
        message.Any(IsUnsafe).ShouldBeFalse(message);
    }

    [Fact]
    public void Ambiguous_IsEscapedInTheNameAndEveryListedId()
    {
        var message = new ProjectNameAmbiguousException("ac\u001bme", ["bad\u000did", "good"]).Message;

        message.ShouldContain("\\u001B");
        message.ShouldContain("\\u000D");
        message.Any(IsUnsafe).ShouldBeFalse(message);
    }

    private static bool IsUnsafe(char ch) =>
        CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse;
}
