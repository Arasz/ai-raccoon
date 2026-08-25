using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.TestInfra;

/// <summary>
///     Proves <c>[RetryFact]</c> re-runs a failed test and passes on a later attempt. Also the
///     gate probe: it carries a retry attribute, so the Speed/Category gates must see it
///     (IsAssignableFrom) or they pass vacuously.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RetryFactRetriesOnTransientFailureTests
{
    private static int _attempts;

    [RetryFact(2)]
    public void RetriesUntilItPasses()
    {
        _attempts++;
        if (_attempts == 1)
        {
            throw new InvalidOperationException("transient failure on attempt 1");
        }

        _attempts.ShouldBe(2);
    }
}
