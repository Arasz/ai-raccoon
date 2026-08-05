using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>The startup bootstrap warns (never fails) when the bundled model is unavailable (see docs/work/features-native-memory/native-memory.feature).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingBootstrapTests
{
    [Fact]
    public async Task EmbeddingBootstrap_OnMissingAssets_WritesActionableWarning()
    {
        var stderr = new StringWriter();

        await EmbeddingBootstrap.EnsureAtStartupAsync(stderr,
            _ => Task.FromResult(new BundledModelResult(false, ["e1"])),
            TestContext.Current.CancellationToken);

        stderr.ToString().ShouldContain("model set local");
        stderr.ToString().ShouldContain("e1");
    }

    [Fact]
    public async Task EmbeddingBootstrap_OnAllPresent_WritesNoWarning()
    {
        var stderr = new StringWriter();

        await EmbeddingBootstrap.EnsureAtStartupAsync(stderr,
            _ => Task.FromResult(new BundledModelResult(true, [])),
            TestContext.Current.CancellationToken);

        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task EmbeddingBootstrap_WhenEnsureThrows_WritesWarning()
    {
        // Pins the bare-catch path (e.g. the 30 s timeout surfacing as OperationCanceledException):
        // the boot must continue with a warning, never a throw.
        var stderr = new StringWriter();

        await EmbeddingBootstrap.EnsureAtStartupAsync(stderr,
            _ => throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);

        stderr.ToString().ShouldContain("model set local");
    }
}
