using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>The rules every MCP tool shares: reject a blank project id before the access check, and carry the queue meta on every envelope.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolGateTests
{
    private static (RecordingGuard Guard, FakePromotionQueue Queue, ToolGate Gate) NewStack()
    {
        var guard = new RecordingGuard();
        var queue = new FakePromotionQueue();
        return (guard, queue, new ToolGate(guard, queue));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RequireAsync_RejectsABlankProjectId_BeforeTheAccessCheck(string? projectId)
    {
        var (guard, _, gate) = NewStack();

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync(projectId, AccessRequirement.Write, "memory_write",
                TestContext.Current.CancellationToken));

        ex.Message.ShouldBe("invalid-params: project_id is required");
        guard.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequireAsync_PassesTheRequirementAndToolNameToTheGuard()
    {
        var (guard, _, gate) = NewStack();

        await gate.RequireAsync("acme", AccessRequirement.Destructive, "memory_delete",
            TestContext.Current.CancellationToken);

        guard.Calls.ShouldBe([("acme", AccessRequirement.Destructive, "memory_delete")]);
    }

    [Fact]
    public async Task WrapAsync_CarriesTheQueueMeta()
    {
        var (_, queue, gate) = NewStack();
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
        var (_, queue, gate) = NewStack();

        await gate.WrapAsync("acme", "payload", TestContext.Current.CancellationToken);

        queue.LastMetaProject.ShouldBe("acme");
    }

    /// <summary>memory_promotion_list is the one tool that may name no project; its meta stays
    /// bank-wide (a scalar count), because that call deliberately spans every queue.</summary>
    [Fact]
    public async Task WrapAsync_PassesNullThrough_WhenTheCallNamedNoProject()
    {
        var (_, queue, gate) = NewStack();

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
}
