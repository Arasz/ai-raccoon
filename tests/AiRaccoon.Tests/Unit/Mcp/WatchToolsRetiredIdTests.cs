using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Core.Watch;
using AiRaccoon.Tests;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Projects;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Package E1 watch boundaries: the MCP watch tools inherit the gate's retired-id rule —
///     add/remove under a dropped id refuse before the service is touched, while an alias loser
///     reaches the service already folded to the winner.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class WatchToolsRetiredIdTests
{
    private const string Loser = "old-slug";
    private const string Winner = "new-slug";
    private const string Dropped = "qa-noise-project";

    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry(Loser, Winner)],
        [Winner],
        [Dropped]);

    private static WatchTools WiredTools(RecordingWatchService watch) =>
        new(watch,
            new ToolGate(new AllowAllGuard(), new FakePromotionQueue(),
                new NeverMigratingStore(), new AllowingRegistrationGuard(), new StubMigrationGate(true)));

    [Fact]
    public async Task Add_UnderDroppedId_RefusesBeforeTheServiceIsTouched()
    {
        var watch = new RecordingWatchService();
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            var ex = await Should.ThrowAsync<RetiredProjectException>(() =>
                WiredTools(watch).Add(Dropped, "/repo", TestContext.Current.CancellationToken));

            ex.Message.ShouldContain(Dropped);
            watch.AddCalls.ShouldBeEmpty();
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    [Fact]
    public async Task Remove_UnderDroppedId_RefusesBeforeTheServiceIsTouched()
    {
        var watch = new RecordingWatchService();
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            await Should.ThrowAsync<RetiredProjectException>(() =>
                WiredTools(watch).Remove(Dropped, "/repo", TestContext.Current.CancellationToken));

            watch.RemoveCalls.ShouldBeEmpty();
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    [Fact]
    public async Task Add_UnderAliasLoser_ReachesTheServiceFoldedToTheWinner()
    {
        var watch = new RecordingWatchService();
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            var envelope = await WiredTools(watch)
                .Add(Loser, "/repo", TestContext.Current.CancellationToken);

            watch.AddCalls.ShouldBe([(Winner, "/repo")]);
            envelope.Data.ShouldNotBeNull();
            envelope.Data.ProjectId.ShouldBe(Winner);
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    private sealed class RecordingWatchService : IWatchService
    {
        public List<(string ProjectId, string Path)> AddCalls { get; } = [];
        public List<(string ProjectId, string Path)> RemoveCalls { get; } = [];

        public Task<WatchAddOutcome> AddAsync(string projectId, string path,
            CancellationToken cancellationToken = default)
        {
            AddCalls.Add((projectId, path));
            return Task.FromResult(new WatchAddOutcome([], null));
        }

        public Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default)
        {
            RemoveCalls.Add((projectId, path));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchStatus>>([]);

        public Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> IsPathAllowedAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }

    private sealed class AllowAllGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
