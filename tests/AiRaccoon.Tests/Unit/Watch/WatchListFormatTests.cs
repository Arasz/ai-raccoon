using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>
///     Byte-pins the watch-list block format: labeled header, one indented line per
///     scope path, "(none)" for an empty scope. Ordering is the CLI's job, not the
///     formatter's (single-target signature).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class WatchListFormatTests
{
    [Fact]
    public void Render_EmptyScope_RendersNoneInline()
    {
        WatchListFormat.Render("global", new WatchConfig(false, [], 4))
            .ShouldBe("target: global  enabled: false  concurrency: 4  scope: (none)");
    }

    [Fact]
    public void Render_ScopePaths_OneIndentedLinePerPath()
    {
        WatchListFormat.Render("acme", new WatchConfig(true, ["/a", "/b"], 8))
            .ShouldBe("target: acme  enabled: true  concurrency: 8  scope:\n  /a\n  /b");
    }

    [Fact]
    public void Render_EnabledAndConcurrency_RenderCanonicalValues()
    {
        WatchListFormat.Render("x", new WatchConfig(true, [], 4))
            .ShouldBe("target: x  enabled: true  concurrency: 4  scope: (none)");
    }
}
