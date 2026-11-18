using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchConfigKeysTests
{
    [Theory]
    [InlineData("watch.enabled.global", true)]
    [InlineData("watch.enabled.0199a1b2-0000-7000-8000-0000000000c2", true)]
    [InlineData("watch.concurrency.global", false)]
    [InlineData("watch.buffer-size.global", false)]
    [InlineData("noise.enabled.global", false)]
    [InlineData("watch.enabled", false)]
    [InlineData("", false)]
    public void IsEnabledKey_RecognisesTheGlobalAndPerProjectEnabledKeys(string key, bool expected) =>
        WatchConfigKeys.IsEnabledKey(key).ShouldBe(expected);

    [Fact]
    public void IsEnabledKey_AcceptsWhatTheKeyHelpersProduce()
    {
        WatchConfigKeys.IsEnabledKey(WatchConfigKeys.EnabledGlobal).ShouldBeTrue();
        WatchConfigKeys.IsEnabledKey(WatchConfigKeys.EnabledProject("acme")).ShouldBeTrue();
    }
}
