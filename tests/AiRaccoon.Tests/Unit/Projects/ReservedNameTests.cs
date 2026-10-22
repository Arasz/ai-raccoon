using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
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
    ///     The mint tool refuses the reserved name instead of registering a private project under
    ///     it. The refusal names the reserved target, so the caller knows the name is taken.
    /// </summary>
    [Fact]
    public async Task MintingTheReservedName_IsRefused()
    {
        var registry = new RecordingRegistry();
        var directory = new StubDirectory();
        var tools = new ProjectTools(registry, directory, new StubGate());

        var error = await Should.ThrowAsync<ArgumentException>(() =>
            tools.Get("shared", TestContext.Current.CancellationToken));

        error.Message.ShouldContain("reserved");
        error.Message.ShouldContain("shared");
        registry.Registered.ShouldBeEmpty("a refused mint registers nothing");
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

    /// <summary>The registry never sees a registration whose name is the reserved literal.</summary>
    private sealed class RecordingRegistry : IProjectRegistry
    {
        public List<(string ProjectId, string? Name)> Registered { get; } = [];

        public Task RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
        {
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
