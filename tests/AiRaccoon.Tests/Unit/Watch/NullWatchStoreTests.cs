using AiRaccoon.Infrastructure.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>The no-watches null object answers every fingerprint read as empty.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NullWatchStoreTests
{
    [Fact]
    public async Task ListFileStampsAsync_ReturnsNoStamps() =>
        (await NullWatchStore.Instance.ListFileStampsAsync("acme", TestContext.Current.CancellationToken))
        .ShouldBeEmpty();
}
