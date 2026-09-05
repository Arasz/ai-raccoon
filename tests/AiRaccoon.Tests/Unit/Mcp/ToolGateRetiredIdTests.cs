using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using AiRaccoon.Tests;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

using AiRaccoon.Tests.Unit.Projects;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Package E1 at the ToolGate write path: a write under a DROPPED id is refused with an error
///     naming the repair attribution, while a write under an alias loser folds through to the
///     winner so stale-config writers keep working. Reads never refuse; an unmigrated bank keeps
///     the pre-P3 pass-through.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ToolGateRetiredIdTests
{
    private const string Loser = "old-slug";
    private const string Winner = "new-slug";
    private const string Dropped = "qa-noise-project";

    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry(Loser, Winner)],
        [Winner],
        [Dropped]);

    private static ToolGate MigratedGate(
        RecordingGuard guard,
        RecordingRegistrationGuard registration) =>
        new(guard, new FakePromotionQueue(), new NeverMigratingStore(), registration,
            new StubMigrationGate(true));

    [Theory]
    [InlineData(AccessRequirement.Write)]
    [InlineData(AccessRequirement.Destructive)]
    public async Task RequireAsync_WriteUnderDroppedId_RefusesNamingTheRepairAttribution(
        AccessRequirement requirement)
    {
        // E-AC1. Ledger — dropped-write-refused.
        var guard = new RecordingGuard();
        var registration = new RecordingRegistrationGuard();
        var gate = MigratedGate(guard, registration);
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            var ex = await Should.ThrowAsync<RetiredProjectException>(() =>
                gate.RequireAsync(Dropped, requirement, "memory_write",
                    TestContext.Current.CancellationToken));

            ex.Message.ShouldContain(Dropped);
            ex.Message.ShouldContain("repair");
            guard.Calls.ShouldBeEmpty("a retired id is invalid before any access check runs");
            registration.Calls.ShouldBeEmpty("the dropped refusal wins over project-not-registered");
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    [Fact]
    public async Task RequireAsync_ReadUnderDroppedId_PassesThrough()
    {
        // Reads never refuse: visibility into retired state stays available.
        var guard = new RecordingGuard();
        var registration = new RecordingRegistrationGuard();
        var gate = MigratedGate(guard, registration);
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            var canonical = await gate.RequireAsync(Dropped, AccessRequirement.Read, "memory_search",
                TestContext.Current.CancellationToken);

            canonical.ShouldBe(Dropped);
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    [Fact]
    public async Task RequireAsync_WriteUnderAliasLoser_LandsUnderTheWinner()
    {
        // E-AC2: stale-config writers keep working, data lands canonical.
        var guard = new RecordingGuard();
        var registration = new RecordingRegistrationGuard();
        var gate = MigratedGate(guard, registration);
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            var canonical = await gate.RequireAsync(Loser, AccessRequirement.Write, "memory_write",
                TestContext.Current.CancellationToken);

            canonical.ShouldBe(Winner);
            guard.Calls.ShouldBe([(Winner, AccessRequirement.Write, "memory_write")]);
            registration.Calls.ShouldBe([(Winner, AccessRequirement.Write)]);
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    [Fact]
    public async Task RequireAsync_WhenExplicitlyUnmigrated_PassesTheDroppedIdThrough()
    {
        // P3 arms alongside the migration gate (H8): an unmigrated bank behaves exactly as before.
        var guard = new RecordingGuard();
        var registration = new RecordingRegistrationGuard();
        var gate = new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore(), registration,
            new StubMigrationGate(false));
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            var canonical = await gate.RequireAsync(Dropped, AccessRequirement.Write, "memory_write",
                TestContext.Current.CancellationToken);

            canonical.ShouldBe(Dropped);
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    private sealed class RecordingGuard : IMemoryAccessGuard
    {
        public List<(string ProjectId, AccessRequirement Requirement, string ToolName)> Calls { get; } = [];

        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((projectId, requirement, toolName));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRegistrationGuard : IProjectRegistrationGuard
    {
        public List<(string ProjectId, AccessRequirement Requirement)> Calls { get; } = [];

        public Task EnsureAsync(string projectId, AccessRequirement requirement,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((projectId, requirement));
            return Task.CompletedTask;
        }
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }
}
