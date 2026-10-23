using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>
///     The reserved shared target owns the literal id <c>shared</c> (ADR-0137). Its NAME is
///     reserved too: a private project registered under the name <c>shared</c> would shadow it —
///     name lookup would keep answering with the reserved id, so the private project would become
///     unreachable, and the reserved target would publish over rows the ADR says must block it.
///     So the name is not mintable, and a bank that already holds such a row blocks the target.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReservedNameTests
{
    /// <summary>
    ///     The registry refuses the reserved name; the wire text starts exactly with the
    ///     <c>reserved-name:</c> prefix, with no <c>invalid-argument:</c> in front of it.
    /// </summary>
    [Fact]
    public async Task MintingTheReservedName_IsRefusedOnTheWireAsReservedName()
    {
        var registry = new RecordingRegistry();
        var tools = new ProjectTools(registry, new StubDirectory(), new StubGate());
        McpRequestHandler<CallToolRequestParams, CallToolResult> next = async (_, token) =>
        {
            await tools.Get("shared", token);
            return new CallToolResult();
        };

        var result = await ToolRefusals.Filter(next)(Request("save_project_id_token"), TestContext.Current.CancellationToken);

        result.IsError.ShouldBe(true);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        text.ShouldStartWith("reserved-name: ");
        text.ShouldNotContain("invalid-argument");
        text.ShouldNotContain("reserved-name: reserved-name");
    }

    /// <summary>
    ///     A name that is merely reserved-looking is not refused — the check is exact and
    ///     case-sensitive, like the reserved id itself.
    /// </summary>
    [Fact]
    public async Task ANameThatIsNotTheReservedLiteral_IsMinted()
    {
        var registry = new RecordingRegistry();
        var tools = new ProjectTools(registry, new StubDirectory(), new StubGate());

        var envelope = await tools.Get("Shared", TestContext.Current.CancellationToken);

        envelope.Data.ShouldNotBeNull();
        envelope.Data!.ProjectId.ShouldNotBe("shared");
        registry.Registered.ShouldHaveSingleItem();
    }

    private static RequestContext<CallToolRequestParams> Request(string toolName) =>
        new(Substitute.For<McpServer>(),
            new JsonRpcRequest { Method = "tools/call", Id = new RequestId("reserved-1") },
            new CallToolRequestParams { Name = toolName });

    /// <summary>The registry never sees a registration whose name is the reserved literal.</summary>
    private sealed class RecordingRegistry : IProjectRegistry
    {
        public List<(string ProjectId, string? Name)> Registered { get; } = [];

        public Task RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
        {
            if (SharedProjectScope.IsReservedId(name))
            {
                throw new ReservedProjectNameException();
            }

            Registered.Add((projectId, name));
            return Task.CompletedTask;
        }

        public Task<bool> IsRegisteredAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Registered.Any(entry => entry.ProjectId == projectId));

        public Task<bool> HasRowsAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class StubDirectory : IProjectDirectory
    {
        public Task<ProjectRegistration> RegisterAsync(string projectId, string? name,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectRegistration(projectId, ProjectRegistrationOutcome.Registered));

        public Task<IReadOnlyList<string>> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<ProjectIdCheck> CheckAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectIdCheck(projectId, ProjectIdStatus.Unknown));
    }

    private sealed class StubGate : IToolGate
    {
        public Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<string> RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken) =>
            Task.FromResult(projectId ?? string.Empty);

        public Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
            Task.FromResult(new ApiEnvelope<T>(data, new PromotionMeta(0, null)));
    }
}
