using AiRaccoon.Infrastructure.Maintenance;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

/// <summary>
///     Owner feedback on WP11-C: <see cref="PendingEmbedJob" />/<see cref="CodeReindexJob" />
///     enqueue-only <c>RunAsync</c> bodies used <c>Task.FromResult(false)</c> — plumbing for a path
///     that never actually awaits anything. <see cref="IMaintenanceJob" />'s two methods return
///     <see cref="ValueTask{TResult}" /> instead, so a synchronous implementation returns one without
///     allocating a <see cref="Task{TResult}" /> it never needed.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MaintenanceJobShapeTests
{
    [Fact]
    public void RunAsync_ReturnsValueTaskOfBool()
    {
        var method = typeof(IMaintenanceJob).GetMethod(nameof(IMaintenanceJob.RunAsync))!;
        method.ReturnType.ShouldBe(typeof(ValueTask<bool>));
    }

    [Fact]
    public void HasWorkAsync_ReturnsValueTaskOfBool()
    {
        var method = typeof(IMaintenanceJob).GetMethod(nameof(IMaintenanceJob.HasWorkAsync))!;
        method.ReturnType.ShouldBe(typeof(ValueTask<bool>));
    }
}
