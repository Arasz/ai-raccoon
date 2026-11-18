using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Every committed watch change asks the reconcile loop to run now (ADR-0152).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchServiceReconcileSignalTests
{
    private const string Project = "acme";

    [Fact]
    public async Task AddAsync_NewWatch_RequestsAWake()
    {
        using var dir = TempDir.New("signal-add");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);

        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Signal.RequestCount.ShouldBe(1);
    }

    [Fact]
    public async Task AddAsync_ExactReAdd_RequestsAWakeToo()
    {
        using var dir = TempDir.New("signal-readd");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        var outcome = await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        outcome.ShouldNotBeNull();
        stack.Signal.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task RemoveAsync_RequestsAWake()
    {
        using var dir = TempDir.New("signal-remove");
        var stack = new WatchTestStack();
        stack.Enable();
        stack.AllowScope(dir.Path);
        await stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        await stack.Service.RemoveAsync(Project, dir.Path, TestContext.Current.CancellationToken);

        stack.Signal.RequestCount.ShouldBe(2);
    }

    [Fact]
    public async Task AddAsync_DisabledProject_RequestsNoWake()
    {
        using var dir = TempDir.New("signal-disabled");
        var stack = new WatchTestStack();
        stack.AllowScope(dir.Path);

        await Should.ThrowAsync<WatchDisabledException>(() => stack.Service.AddAsync(Project, dir.Path, TestContext.Current.CancellationToken));

        stack.Signal.RequestCount.ShouldBe(0);
    }
}
