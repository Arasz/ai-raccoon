using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.TestInfra;

/// <summary>
///     Proves <c>[RetryTheory]</c> re-runs only the failed data row and passes on a later attempt. Also the
///     gate probe: it carries a retry attribute, so the Speed/Category gates must see it (IsAssignableFrom)
///     or they pass vacuously.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RetryTheoryRetriesDataRowTests
{
    private static readonly Dictionary<int, int> Attempts = [];

    [RetryTheory(2)]
    [InlineData(1)]
    [InlineData(2)]
    public void RetriesOnlyTheFailedRow(int value)
    {
        Attempts[value] = Attempts.GetValueOrDefault(value) + 1;

        if (value == 1 && Attempts[1] == 1)
        {
            throw new InvalidOperationException("transient failure on row 1, attempt 1");
        }

        if (value == 1)
        {
            Attempts[1].ShouldBe(2, "the failed row must be retried to its second attempt");
        }
        else
        {
            Attempts[2].ShouldBe(1, "a passing row must not be re-run when another row retries");
        }
    }
}
