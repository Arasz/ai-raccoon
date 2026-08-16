using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>The rules every MCP tool shares: reject a blank project id before the access check, refuse every call while a model migration is open (ADR-0076), and carry the queue meta on every envelope.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolGateTests
{
    private static (RecordingGuard Guard, FakePromotionQueue Queue, RecordingMigrations Migrations, ToolGate Gate) NewStack()
    {
        var guard = new RecordingGuard();
        var queue = new FakePromotionQueue();
        var migrations = new RecordingMigrations();
        return (guard, queue, migrations, new ToolGate(guard, queue, migrations));
    }

    [Fact]
    public async Task RequireAsync_WhileAModelMigrationIsOpen_Refuses()
    {
        var (guard, _, migrations, gate) = NewStack();
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
        var (guard, _, migrations, gate) = NewStack();
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
        var (guard, _, _, gate) = NewStack();

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync(projectId, AccessRequirement.Write, "memory_write",
                TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("invalid-params: project_id is required");
        guard.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequireAsync_PassesTheRequirementAndToolNameToTheGuard()
    {
        var (guard, _, _, gate) = NewStack();

        await gate.RequireAsync("acme", AccessRequirement.Destructive, "memory_delete",
            TestContext.Current.CancellationToken);

        guard.Calls.ShouldBe([("acme", AccessRequirement.Destructive, "memory_delete")]);
    }

    [Fact]
    public async Task WrapAsync_CarriesTheQueueMeta()
    {
        var (_, queue, _, gate) = NewStack();
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
        var (_, queue, _, gate) = NewStack();

        await gate.WrapAsync("acme", "payload", TestContext.Current.CancellationToken);

        queue.LastMetaProject.ShouldBe("acme");
    }

    /// <summary>memory_promotion_list is the one tool that may name no project; its meta stays
    /// bank-wide (a scalar count), because that call deliberately spans every queue.</summary>
    [Fact]
    public async Task WrapAsync_PassesNullThrough_WhenTheCallNamedNoProject()
    {
        var (_, queue, _, gate) = NewStack();

        await gate.WrapAsync(null, "payload", TestContext.Current.CancellationToken);

        queue.MetaAsked.ShouldBeTrue();
        queue.LastMetaProject.ShouldBeNull();
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

    private sealed class RecordingMigrations : IModelMigrationStore
    {
        public bool HasOpen { get; set; }

        public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(HasOpen);

        public Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by ToolGate.");
    }
}
