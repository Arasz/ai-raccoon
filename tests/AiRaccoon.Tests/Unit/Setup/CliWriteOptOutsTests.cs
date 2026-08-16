using AiRaccoon.Settings;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP7-T1 (ADR-0075 §5.3): the CLI-composition-root opt-out list, asserted rather than implied.
///     Exactly two families write the bank directly instead of through the server — <c>encryption</c>
///     (the bootstrap path, §5.1) and <c>model set</c> (its progress/cancellation shape over HTTP is
///     unruled, §10.3, so it stays a CLI writer until that is designed). Every other command path
///     routes through <see cref="ServerSettingsStore" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliWriteOptOutsTests
{
    [Theory]
    [InlineData("encryption", "bitwarden")]
    [InlineData("encryption", "show")]
    [InlineData("encryption", "unset")]
    [InlineData("encryption", "migrate")]
    [InlineData("model", "set", "local")]
    [InlineData("model", "set", "openai")]
    public void WritesDirectly_IsTrue_ForTheDeclaredOptOuts(params string[] commandPath) =>
        CliWriteOptOuts.WritesDirectly(commandPath).ShouldBeTrue();

    [Theory]
    [InlineData("settings", "sweep", "threshold", "set")]
    [InlineData("settings", "access", "default", "show")]
    [InlineData("settings", "model", "show")]
    [InlineData("watch", "registered")]
    [InlineData("extract", "prune")]
    [InlineData("serve")]
    [InlineData("serve", "observability")]
    [InlineData("model", "reset")]
    public void WritesDirectly_IsFalse_ForEverythingElse(params string[] commandPath) =>
        CliWriteOptOuts.WritesDirectly(commandPath).ShouldBeFalse();

    /// <summary>A "model" path that is not "model set" (e.g. a future "model show") must not match on the prefix alone.</summary>
    [Fact]
    public void WritesDirectly_RequiresModelSetExactly_NotJustTheModelFamily()
    {
        CliWriteOptOuts.WritesDirectly(["model"]).ShouldBeFalse();
        CliWriteOptOuts.WritesDirectly(["model", "reset"]).ShouldBeFalse();
    }
}
