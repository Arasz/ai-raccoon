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
///     winner so stale-config writers keep working. Reads under a dropped id are refused too (D2),
///     and neither depends on the repair finish marker.
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

    private static ToolGate NewGate(
        RecordingGuard guard,
        RecordingRegistrationGuard registration) =>
        new(guard, new FakePromotionQueue(), new NeverMigratingStore(), registration);

    [Theory]
    [InlineData(AccessRequirement.Write)]
    [InlineData(AccessRequirement.Destructive)]
    public async Task RequireAsync_WriteUnderDroppedId_RefusesNamingTheRepairAttribution(
        AccessRequirement requirement)
    {
        // E-AC1. Ledger — dropped-write-refused.
        var guard = new RecordingGuard();
        var registration = new RecordingRegistrationGuard();
        var gate = NewGate(guard, registration);
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
    public async Task RequireAsync_ReadUnderDroppedId_IsRefusedAsRetired()
    {
        // D2: a retired id is refused on reads too.
        var guard = new RecordingGuard();
        var registration = new RecordingRegistrationGuard();
        var gate = NewGate(guard, registration);
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            await Should.ThrowAsync<RetiredProjectException>(() =>
                gate.RequireAsync(Dropped, AccessRequirement.Read, "memory_search",
                    TestContext.Current.CancellationToken));

            guard.Calls.ShouldBeEmpty();
            registration.Calls.ShouldBeEmpty();
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
        var gate = NewGate(guard, registration);
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

    /// <summary>A repair request resets the finish marker, so the fold must not depend on it.</summary>
    [Theory]
    [InlineData(AccessRequirement.Read)]
    [InlineData(AccessRequirement.Write)]
    public async Task RequireAsync_DroppedIdWithOpenRepairRequest_IsStillRefused(AccessRequirement requirement)
    {
        var gate = new ToolGate(new RecordingGuard(), new FakePromotionQueue(), new NeverMigratingStore(),
            new RecordingRegistrationGuard());
        try
        {
            ProjectIdAliasMap.ReplaceDefault(FixtureMap());

            await Should.ThrowAsync<RetiredProjectException>(() =>
                gate.RequireAsync(Dropped, requirement, "memory_search",
                    TestContext.Current.CancellationToken));
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
}
