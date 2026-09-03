using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using AiRaccoon.Tests;
using AiRaccoon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>The rules every MCP tool shares: reject a blank project id before the access check, refuse every call while a model migration is open (ADR-0076), refuse an unregistered id on a write (ADR-0089), and carry the queue meta on every envelope.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolGateTests
{
    private static (RecordingGuard Guard, FakePromotionQueue Queue, RecordingMigrations Migrations,
        RecordingRegistrationGuard Registration, ToolGate Gate) NewStack()
    {
        var guard = new RecordingGuard();
        var queue = new FakePromotionQueue();
        var migrations = new RecordingMigrations();
        var registration = new RecordingRegistrationGuard();
        return (guard, queue, migrations, registration, new ToolGate(guard, queue, migrations, registration, new NeverMigratedGate()));
    }

    [Fact]
    public async Task RequireAsync_WhileAModelMigrationIsOpen_Refuses()
    {
        var (guard, _, migrations, _, gate) = NewStack();
        migrations.HasOpen = true;

        var ex = await Should.ThrowAsync<ModelMigrationInProgressException>(() =>
            gate.RequireAsync("acme", AccessRequirement.Write, "memory_write",
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("model migration");
        guard.Calls.ShouldBeEmpty(); // refused before the access check even runs
    }

    [Fact]
    public async Task RequireAsync_WhileNoModelMigrationIsOpen_ProceedsAsUsual()
    {
        var (guard, _, migrations, _, gate) = NewStack();
        migrations.HasOpen = false;

        await gate.RequireAsync("acme", AccessRequirement.Write, "memory_write",
            TestContext.Current.CancellationToken);

        guard.Calls.ShouldBe([("acme", AccessRequirement.Write, "memory_write")]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequireAsync_RejectsABlankProjectId_BeforeTheAccessCheck(string? projectId)
    {
        var (guard, _, _, _, gate) = NewStack();

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync(projectId, AccessRequirement.Write, "memory_write",
                TestContext.Current.CancellationToken));

        // Cwd-tolerant: the enriched refusal names the probed working directory (Unit runs beside
        // Integration suites that mutate the process cwd), so pin the stable prefix only.
        ex.Message.ShouldStartWith(
            "invalid-params: projectId is required (no registered project's scope contains cwd ");
        guard.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequireAsync_PassesTheRequirementAndToolNameToTheGuard()
    {
        var (guard, _, _, _, gate) = NewStack();

        await gate.RequireAsync("acme", AccessRequirement.Destructive, "memory_delete",
            TestContext.Current.CancellationToken);

        guard.Calls.ShouldBe([("acme", AccessRequirement.Destructive, "memory_delete")]);
    }

    [Fact]
    public async Task WrapAsync_CarriesTheQueueMeta()
    {
        var (_, queue, _, _, gate) = NewStack();
        // Every field carries a value the queue alone could have supplied: a fabricated empty
        // meta would still satisfy ShouldNotBeNull on a non-nullable record.
        queue.Meta = new PromotionMeta(7, 42.5) { Capacity = new PromotionCapacityInfo(50, 7, false) };

        var envelope = await gate.WrapAsync("acme", "payload", TestContext.Current.CancellationToken);

        envelope.Data.ShouldBe("payload");
        envelope.Meta.ShouldBe(queue.Meta);
    }

    /// <summary>C5: the meta is scoped to the caller's project, so the gate must hand the queue the
    /// project id the tool was called with — a bank-wide read leaks every other project's queue.</summary>
    [Fact]
    public async Task WrapAsync_AsksTheQueueForTheCallersProjectOnly()
    {
        var (_, queue, _, _, gate) = NewStack();

        await gate.WrapAsync("acme", "payload", TestContext.Current.CancellationToken);

        queue.LastMetaProject.ShouldBe("acme");
    }

    /// <summary>memory_promotion_list is the one tool that may name no project; its meta stays
    /// bank-wide (a scalar count), because that call deliberately spans every queue.</summary>
    [Fact]
    public async Task WrapAsync_PassesNullThrough_WhenTheCallNamedNoProject()
    {
        var (_, queue, _, _, gate) = NewStack();

        await gate.WrapAsync(null, "payload", TestContext.Current.CancellationToken);

        queue.MetaAsked.ShouldBeTrue();
        queue.LastMetaProject.ShouldBeNull();
    }

    /// <summary>
    ///     ADR-0089: the registration guard is called for every requirement, carrying the canonical
    ///     id — same shape as the access guard. The guard itself decides whether Read is exempt
    ///     (ProjectRegistrationGuardTests), not ToolGate.
    /// </summary>
    [Theory]
    [InlineData(AccessRequirement.Read)]
    [InlineData(AccessRequirement.Write)]
    [InlineData(AccessRequirement.Destructive)]
    public async Task RequireAsync_CallsTheRegistrationGuard_WithTheCanonicalIdAndTheRequirement(
        AccessRequirement requirement)
    {
        var (_, _, _, registration, gate) = NewStack();

        await gate.RequireAsync("{ACME}", requirement, "memory_write", TestContext.Current.CancellationToken);

        // "{ACME}" is not a guid, so ProjectId.TryCanonicalize passes it through unchanged.
        registration.Calls.ShouldBe([("{ACME}", requirement)]);
    }

    [Fact]
    public async Task RequireAsync_WhenTheAccessGuardRefuses_DoesNotReachTheRegistrationGuard()
    {
        // Registration is checked AFTER access: an unauthorized caller must not be able to
        // distinguish "unregistered" from "registered" from the refusal shape (ADR-0089 review).
        var (guard, _, _, registration, gate) = NewStack();
        guard.Refuse = true;

        await Should.ThrowAsync<AccessDeniedException>(() =>
            gate.RequireAsync("acme", AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));

        registration.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequireAsync_WhenTheRegistrationGuardRefuses_PropagatesAfterAccessPassed()
    {
        var (guard, _, _, registration, gate) = NewStack();
        registration.Refuse = true;

        await Should.ThrowAsync<UnregisteredProjectException>(() =>
            gate.RequireAsync("acme", AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));

        guard.Calls.ShouldBe([("acme", AccessRequirement.Write, "memory_write")],
            "access passed first; only then did the registration refusal fire");
    }

    /// <summary>
    ///     P3 activation (review M1): once the P2 finished marker exists, a known loser folds to
    ///     its winner at the choke — every downstream guard and store sees jsaa, never the loser.
    ///     Ledger — skip-alias-fold : --filter RequireAsync_WhenMigrated_FoldsAKnownAliasToTheWinner :
    ///     job-search-ai-assistant write.
    /// </summary>
    [Fact]
    public async Task RequireAsync_WhenMigrated_FoldsAKnownAliasToTheWinner()
    {
        var (guard, _, _, registration, _) = NewStack();
        var gate = new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore(), registration,
            migrationGate: new StubMigrationGate(true));

        var canonical = await gate.RequireAsync("job-search-ai-assistant", AccessRequirement.Write,
            "memory_write", TestContext.Current.CancellationToken);

        canonical.ShouldBe("jsaa");
        guard.Calls.ShouldBe([("jsaa", AccessRequirement.Write, "memory_write")]);
        registration.Calls.ShouldBe([("jsaa", AccessRequirement.Write)]);
    }

    /// <summary>
    ///     The mechanical half of M1: an EXPLICITLY unmigrated bank behaves exactly as before —
    ///     the loser passes through unfolded (the winners' own writes must never refuse on an
    ///     unmigrated bank). d-425 SHOULD-1 inversion: the old theory's `true` leg constructed the
    ///     gate with NO migration gate at all and asserted the pass-through bypass — that shape no
    ///     longer compiles (the ctor takes no default), so every construction names its migration
    ///     state and only an explicit unmigrated gate passes through.
    ///     Ledger — missing-gate-pass-through : --filter RequireAsync_WhenExplicitlyUnmigrated_PassesTheLoserThroughUnfolded : loser write, explicit unmigrated gate.
    /// </summary>
    [Fact]
    public async Task RequireAsync_WhenExplicitlyUnmigrated_PassesTheLoserThroughUnfolded()
    {
        var (guard, _, _, registration, _) = NewStack();
        var gate = new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore(), registration,
            new StubMigrationGate(false));

        var canonical = await gate.RequireAsync("job-search-ai-assistant", AccessRequirement.Write,
            "memory_write", TestContext.Current.CancellationToken);

        canonical.ShouldBe("job-search-ai-assistant");
        guard.Calls.ShouldBe([("job-search-ai-assistant", AccessRequirement.Write, "memory_write")]);
    }

    private sealed class RecordingGuard : IMemoryAccessGuard
    {
        public List<(string ProjectId, AccessRequirement Requirement, string ToolName)> Calls { get; } = [];
        public bool Refuse { get; set; }

        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((projectId, requirement, toolName));
            if (Refuse)
            {
                throw new AccessDeniedException($"{toolName} requires mode full (current rw)");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMigrations : IModelMigrationStore
    {
        public bool HasOpen { get; set; }

        public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(HasOpen);

        public Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by ToolGate.");
    }

    private sealed class RecordingRegistrationGuard : IProjectRegistrationGuard
    {
        public List<(string ProjectId, AccessRequirement Requirement)> Calls { get; } = [];
        public bool Refuse { get; set; }

        public Task EnsureAsync(string projectId, AccessRequirement requirement, CancellationToken cancellationToken = default)
        {
            Calls.Add((projectId, requirement));
            if (Refuse)
            {
                throw new UnregisteredProjectException(projectId);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }
}
