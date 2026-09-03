using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

/// <summary>
///     The wire/row key each <see cref="RepairKind" /> maps to — shared by the repair_requests table,
///     the maintenance jobs that read it, and the /repair endpoint (ADR-0075 amendment), so a typo
///     here would silently desynchronize CLI request from server drain.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RepairKindsTests
{
    [Theory]
    [InlineData(RepairKind.Reingest, "reingest")]
    [InlineData(RepairKind.ChunkIndex, "chunk-index")]
    [InlineData(RepairKind.ProjectIds, "project-ids")]
    public void ToKey_MatchesTheCliVerbName(RepairKind kind, string expected) =>
        kind.ToKey().ShouldBe(expected);

    [Fact]
    public void ToKey_ForAnUndeclaredValue_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => ((RepairKind)99).ToKey());
}
