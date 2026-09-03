using AiRaccoon.Settings;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP7-T1 (ADR-0075 §5.3), reduced by ADR-0076: the CLI-composition-root opt-out list, asserted
///     rather than implied. Exactly one family writes the bank directly instead of through the
///     server — <c>encryption</c> (the bootstrap path, §5.1). <c>model set</c> used to be here too;
///     §10.3 is now ruled and it routes through <see cref="ServerSettingsStore" /> like every other
///     settings command.
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
    [InlineData("model", "set", "local")]
    [InlineData("model", "set", "openai")]
    // The reported defect this ADR-0075 amendment fixes: `repair <verb> --apply` was never on this
    // list, yet still wrote the bank directly via an unconditionally-bound IMemoryStore
    // (AppRegistrations.cs) — repair is now a thin IRepairStore client like every other command, so
    // these two rows belong here like everything else.
    [InlineData("repair", "reingest")]
    [InlineData("repair", "chunk-index")]
    [InlineData("repair", "project-ids")]
    public void WritesDirectly_IsFalse_ForEverythingElse(params string[] commandPath) =>
        CliWriteOptOuts.WritesDirectly(commandPath).ShouldBeFalse();
}
