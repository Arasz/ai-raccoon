using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     ADR-0089 decision 3: a project exists when it is registered, not when it is first written
///     to. The refusal test is "no registry row AND no rows" — a registered project with no rows,
///     or an unregistered id the bank already holds rows for, are both intended, coexisting states.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectRegistrationGuardTests
{
    private static ProjectRegistrationGuard NewGuard(FakeProjectRegistry registry) => new(registry, NullLogger<ProjectRegistrationGuard>.Instance);

    [Fact]
    public async Task AnUnregisteredGuidV7_IsRefused()
    {
        var guard = NewGuard(new FakeProjectRegistry());

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            guard.EnsureAsync(Guid.CreateVersion7().ToString("D"), AccessRequirement.Write,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnregisteredRawTextId_IsAutoRegistered()
    {
        var registry = new FakeProjectRegistry();
        var guard = NewGuard(registry);

        await guard.EnsureAsync("jsaa", AccessRequirement.Write, TestContext.Current.CancellationToken);

        registry.Registered.ShouldContain("jsaa");
    }

    [Fact]
    public async Task ALegacyRawTextIdWithRows_IsAllowedAndWarns()
    {
        var registry = new FakeProjectRegistry { RowsFor = { "jsaa" } };
        var logger = new FakeLogger<ProjectRegistrationGuard>();
        var guard = new ProjectRegistrationGuard(registry, logger);

        // Does not throw — a raw-text id the bank already holds rows for keeps working.
        await guard.EnsureAsync("jsaa", AccessRequirement.Write, TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().ShouldContain(r =>
                r.Id.Name == "LegacyProjectIdAccepted",
            "the first write through a legacy id warns");
    }

    /// <summary>The docs row promises a ONE-time warning; the guard is a singleton, so the second
    /// write through the same id stays silent (the warned-set dedupes per process).</summary>
    [Fact]
    public async Task ASecondWriteThroughTheSameLegacyId_DoesNotWarnAgain()
    {
        var registry = new FakeProjectRegistry { RowsFor = { "jsaa" } };
        var logger = new FakeLogger<ProjectRegistrationGuard>();
        var guard = new ProjectRegistrationGuard(registry, logger);

        await guard.EnsureAsync("jsaa", AccessRequirement.Write, TestContext.Current.CancellationToken);
        await guard.EnsureAsync("jsaa", AccessRequirement.Write, TestContext.Current.CancellationToken);
        await guard.EnsureAsync("jsaa", AccessRequirement.Destructive, TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot()
            .Count(r => r.Id.Name == "LegacyProjectIdAccepted")
            .ShouldBe(1);
    }

    [Fact]
    public async Task TwoDifferentLegacyIds_EachWarnOnce()
    {
        var registry = new FakeProjectRegistry { RowsFor = { "jsaa", "other-legacy" } };
        var logger = new FakeLogger<ProjectRegistrationGuard>();
        var guard = new ProjectRegistrationGuard(registry, logger);

        await guard.EnsureAsync("jsaa", AccessRequirement.Write, TestContext.Current.CancellationToken);
        await guard.EnsureAsync("other-legacy", AccessRequirement.Write, TestContext.Current.CancellationToken);
        await guard.EnsureAsync("jsaa", AccessRequirement.Write, TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().Count(r => r.Id.Name == "LegacyProjectIdAccepted").ShouldBe(2);
    }

    /// <summary>The #546-review open question: a code-only legacy project (rows in code_entries,
    /// none in entries) is indistinguishable from any other "has rows, not registered" project at
    /// this guard's contract level — the corpus distinction lives in MemorySql.ProjectHasRows,
    /// pinned separately by SqliteProjectRegistryTests.HasRowsAsync_IsTrueForACodeOnlyLegacyProject.</summary>
    [Fact]
    public async Task ACodeOnlyLegacyProjectWithRows_IsAllowedAndWarns()
    {
        var registry = new FakeProjectRegistry { RowsFor = { "code-only-legacy" } };
        var logger = new FakeLogger<ProjectRegistrationGuard>();
        var guard = new ProjectRegistrationGuard(registry, logger);

        await guard.EnsureAsync("code-only-legacy", AccessRequirement.Write, TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().ShouldContain(r =>
                r.Id.Name == "LegacyProjectIdAccepted",
            "a code-only legacy id takes the same warn-and-work path");
    }

    [Fact]
    public async Task ARegisteredId_IsAllowedSilently()
    {
        var canonical = Guid.CreateVersion7().ToString("D");
        var registry = new FakeProjectRegistry { Registered = { canonical } };
        var guard = NewGuard(registry);

        await guard.EnsureAsync(canonical, AccessRequirement.Write, TestContext.Current.CancellationToken);
    }

    /// <summary>The guard's contract receives the CANONICAL form: ToolGate canonicalizes before
    /// calling (ADR-0089 decision 2), so a re-spelled input never reaches here. This pins that
    /// the guard answers on the canonical string it is given.</summary>
    [Fact]
    public async Task ACanonicalRegisteredGuid_IsAllowed()
    {
        var canonical = Guid.CreateVersion7().ToString("D");
        var registry = new FakeProjectRegistry { Registered = { canonical } };
        var guard = NewGuard(registry);

        await guard.EnsureAsync(canonical, AccessRequirement.Write, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AReadRequirement_IsNeverRefused()
    {
        // No registration, no rows — would refuse on Write/Destructive, but Read skips the guard
        // entirely (same shape as MemoryAccessGuard.EnsureAsync's early return for Read).
        var registry = new FakeProjectRegistry();
        var guard = NewGuard(registry);

        await guard.EnsureAsync(Guid.CreateVersion7().ToString("D"), AccessRequirement.Read,
            TestContext.Current.CancellationToken);

        registry.IsRegisteredCalls.ShouldBeEmpty("Read must skip the registry lookups entirely, not just tolerate a false answer");
        registry.HasRowsCalls.ShouldBeEmpty();
    }

    private sealed class FakeProjectRegistry : IProjectRegistry
    {
        public HashSet<string> Registered { get; } = [];
        public HashSet<string> RowsFor { get; } = [];
        public List<string> IsRegisteredCalls { get; } = [];
        public List<string> HasRowsCalls { get; } = [];

        public Task RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
        {
            Registered.Add(projectId);
            return Task.CompletedTask;
        }

        public Task<bool> IsRegisteredAsync(string projectId, CancellationToken cancellationToken = default)
        {
            IsRegisteredCalls.Add(projectId);
            return Task.FromResult(Registered.Contains(projectId));
        }

        public Task<bool> HasRowsAsync(string projectId, CancellationToken cancellationToken = default)
        {
            HasRowsCalls.Add(projectId);
            return Task.FromResult(RowsFor.Contains(projectId));
        }
    }
}
