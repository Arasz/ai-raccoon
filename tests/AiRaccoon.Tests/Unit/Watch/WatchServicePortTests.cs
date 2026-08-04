using AiRaccoon.Core.Watch;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Watch;

/// <summary>Pins the S2 port surface (S4 implements, S6 consumes) and its error types.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class WatchServicePortTests
{
    private sealed class StubWatchService : IWatchService
    {
        public Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchStatus>>([]);

        public Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<bool> IsPathAllowedAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    [Fact]
    public async Task Port_StatusAsync_ReturnsEmptyListWhenNoWatches()
    {
        var statuses = await new StubWatchService().StatusAsync("acme");
        statuses.ShouldBeEmpty();
    }

    [Fact]
    public async Task Port_AddAndRemove_AreAwaitableWithoutThrowing()
    {
        var service = new StubWatchService();
        await service.AddAsync("acme", "/repo");
        await service.RemoveAsync("acme", "/repo");
    }

    [Fact]
    public async Task Port_IsEnabledAndIsPathAllowed_ReturnBooleans()
    {
        var service = new StubWatchService();
        (await service.IsEnabledAsync("acme")).ShouldBeFalse();
        (await service.IsPathAllowedAsync("acme", "/repo")).ShouldBeFalse();
    }

    [Fact]
    public void WatchDisabledException_CarriesProjectIdInMessage() =>
        new WatchDisabledException("acme").Message.ShouldContain("acme");

    [Fact]
    public void PathOutsideScopeException_CarriesPathInMessage() =>
        new PathOutsideScopeException("/repo").Message.ShouldContain("/repo");

    [Fact]
    public void PathNotFound_CarriesPathInMessage() =>
        new PathNotFound("/repo").Message.ShouldContain("/repo");

    [Fact]
    public void ErrorTypes_AreInvalidOperationExceptions()
    {
        new WatchDisabledException("acme").ShouldBeAssignableTo<InvalidOperationException>();
        new PathOutsideScopeException("/repo").ShouldBeAssignableTo<InvalidOperationException>();
        new PathNotFound("/repo").ShouldBeAssignableTo<InvalidOperationException>();
    }
}
