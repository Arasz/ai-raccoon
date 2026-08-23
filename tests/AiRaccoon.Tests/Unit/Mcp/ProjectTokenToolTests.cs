using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     project_id_token_get mints a guidv7, registers it, and returns it (ADR-0089 decision 4). It
///     is the one tool that gates via RequireBankAvailableAsync instead of RequireAsync — there is
///     no project yet to resolve an access mode for.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectTokenToolTests
{
    private readonly FakeProjectRegistry _registry = new();
    private readonly FakeToolGate _gate = new();
    private readonly ProjectTools _tools;

    public ProjectTokenToolTests()
    {
        _tools = new ProjectTools(_registry, _gate);
    }

    [Fact]
    public async Task Get_ReturnsAGuidV7_InCanonicalLowercaseDForm()
    {
        var envelope = await _tools.Get(cancellationToken: TestContext.Current.CancellationToken);

        var id = envelope.Data!.ProjectId;
        Guid.Parse(id).ToString("D").ShouldBe(id);
        id[14].ShouldBe('7'); // the version nibble, at a fixed offset in the canonical D-form
    }

    [Fact]
    public async Task Get_RegistersTheMintedId()
    {
        var envelope = await _tools.Get(cancellationToken: TestContext.Current.CancellationToken);

        _registry.Registered.ShouldContainKey(envelope.Data!.ProjectId);
    }

    [Fact]
    public async Task Get_HonoursAnOptionalName()
    {
        var envelope = await _tools.Get("acme", TestContext.Current.CancellationToken);

        _registry.Registered[envelope.Data!.ProjectId].ShouldBe("acme");
    }

    [Fact]
    public async Task Get_WithNoName_RegistersANullName()
    {
        var envelope = await _tools.Get(cancellationToken: TestContext.Current.CancellationToken);

        _registry.Registered[envelope.Data!.ProjectId].ShouldBeNull();
    }

    [Fact]
    public async Task Get_RefusesWhileAModelMigrationIsOpen()
    {
        _gate.RefuseBankAvailable = true;

        await Should.ThrowAsync<ModelMigrationInProgressException>(() =>
            _tools.Get(cancellationToken: TestContext.Current.CancellationToken));

        _registry.Registered.ShouldBeEmpty();
    }

    private sealed class FakeProjectRegistry : IProjectRegistry
    {
        public Dictionary<string, string?> Registered { get; } = [];

        public Task RegisterAsync(string projectId, string? name, CancellationToken cancellationToken = default)
        {
            Registered[projectId] = name;
            return Task.CompletedTask;
        }

        public Task<bool> IsRegisteredAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Registered.ContainsKey(projectId));

        public Task<bool> HasRowsAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeToolGate : IToolGate
    {
        public bool RefuseBankAvailable { get; set; }

        public Task RequireBankAvailableAsync(string toolName, CancellationToken cancellationToken)
        {
            if (RefuseBankAvailable)
            {
                throw new ModelMigrationInProgressException(
                    "ai-raccoon: a model migration is in progress; try again once it finishes");
            }

            return Task.CompletedTask;
        }

        public Task RequireAsync(string? projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("project_id_token_get never resolves an access mode.");

        public Task<ApiEnvelope<T>> WrapAsync<T>(string? projectId, T data, CancellationToken cancellationToken) =>
            Task.FromResult(new ApiEnvelope<T>(data, new PromotionMeta(0, null)));
    }
}
