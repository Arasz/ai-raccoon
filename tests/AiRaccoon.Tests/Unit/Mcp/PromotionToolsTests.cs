using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>The propose-tier review surface: memory_promotion_list must not crowd out an agent's
/// context with the full stored value of every queued row.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionToolsTests
{
    private static (FakePromotionQueue Queue, PromotionTools Tools) NewStack()
    {
        var queue = new FakePromotionQueue();
        var gate = new ToolGate(new AllowingGuard(), queue, new NeverMigratingStore(), new AllowingRegistrationGuard());
        return (queue, new PromotionTools(queue, gate));
    }

    private static PromotionQueueRow Row(string hash, string value) => new("acme", hash, $"{hash}.md", value, null, 2.0, ["organic-note"], 1, 1);

    [Fact]
    public async Task List_TruncatesTheValue_ByDefault()
    {
        var (queue, tools) = NewStack();
        queue.Rows = [Row("h1", new string('x', 5000))];

        var envelope = await tools.List("acme", cancellationToken: TestContext.Current.CancellationToken);

        var row = envelope.Data!.Rows.ShouldHaveSingleItem();
        row.Value.Length.ShouldBeLessThan(5000);
    }

    [Fact]
    public async Task List_IncludesTheFullValue_WhenRequested()
    {
        var (queue, tools) = NewStack();
        var value = new string('x', 5000);
        queue.Rows = [Row("h1", value)];

        var envelope = await tools.List("acme", includeFullValue: true,
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Rows.ShouldHaveSingleItem().Value.ShouldBe(value);
    }

    [Fact]
    public async Task List_ShortValue_IsUnchanged()
    {
        var (queue, tools) = NewStack();
        queue.Rows = [Row("h1", "a short queued fact")];

        var envelope = await tools.List("acme", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Rows.ShouldHaveSingleItem().Value.ShouldBe("a short queued fact");
    }

    [Fact]
    public async Task List_WithoutProjectId_RefusesWithoutAllProjects()
    {
        var (queue, tools) = NewStack();
        queue.Rows = [Row("h1", "cross-project row")];

        var ex = await Should.ThrowAsync<McpException>(() =>
            tools.List(cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: ");
        ex.Message.ShouldContain("allProjects");
        ex.Message.ShouldContain("projectId");
    }

    [Fact]
    public async Task List_WithoutProjectId_ListsAcrossProjects_WhenAllProjectsIsTrue()
    {
        var (queue, tools) = NewStack();
        queue.Rows = [Row("h1", "cross-project row")];

        var envelope = await tools.List(allProjects: true, cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Rows.ShouldHaveSingleItem();
        queue.LastListProject.ShouldBeNull("allProjects=true still asks the queue for every project");
    }

    [Fact]
    public async Task List_WithProjectId_IsUnaffectedByAllProjects()
    {
        var (queue, tools) = NewStack();
        queue.Rows = [Row("h1", "a short queued fact")];

        var envelope = await tools.List("acme", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Rows.ShouldHaveSingleItem();
        queue.LastListProject.ShouldBe("acme");
    }

    /// <summary>
    ///     P4 boundary pin: "" is a NAMED id (blank → the gate's cwd rule), never a silent null —
    ///     treating it as omitted would swap the refusal and scope the meta bank-wide.
    ///     Ledger — treat-empty-as-null : --filter List_EmptyString_GoesToTheGate_NotTreatedAsOmitted :
    ///     "" projectId without a resolver wired.
    /// </summary>
    [Fact]
    public async Task List_EmptyString_GoesToTheGate_NotTreatedAsOmitted()
    {
        var (_, tools) = NewStack();

        var ex = await Should.ThrowAsync<McpException>(() =>
            tools.List("", cancellationToken: TestContext.Current.CancellationToken));

        // The gate's blank-id refusal, NOT the omitted-id allProjects refusal.
        ex.Message.ShouldStartWith(
            "invalid-params: projectId is required (no registered project's scope contains cwd ");
        ex.Message.ShouldNotContain("allProjects");
    }

    /// <summary>
    ///     The allProjects branch meets the gate too: with no project to check, the migration lock
    ///     still holds (ADR-0076 refuses every bank operation for the duration).
    ///     Ledger — skip-bank-check : --filter List_AllProjects_WhileAModelMigrationIsOpen_Refuses :
    ///     allProjects=true with an open migration.
    /// </summary>
    [Fact]
    public async Task List_AllProjects_WhileAModelMigrationIsOpen_Refuses()
    {
        var queue = new FakePromotionQueue();
        var gate = new ToolGate(new AllowingGuard(), queue, new MigratingStore(), new AllowingRegistrationGuard());
        var tools = new PromotionTools(queue, gate);

        await Should.ThrowAsync<ModelMigrationInProgressException>(() =>
            tools.List(allProjects: true, cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed class AllowingGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class MigratingStore : IModelMigrationStore
    {
        public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Not exercised by PromotionTools.");
    }
}
