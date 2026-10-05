using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     project_id_get looks an id up by its exact name: one match is returned, none or several
///     refuse, and it never registers anything or asks the registration gate about a caller id.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdGetToolTests
{
    private const string First = "0199a1b2-0000-7000-8000-000000000001";
    private const string Second = "0199a1b2-0000-7000-8000-000000000002";

    private readonly IProjectRegistry _registry = Substitute.For<IProjectRegistry>();
    private readonly IProjectDirectory _directory = Substitute.For<IProjectDirectory>();
    private readonly IToolGate _gate = Substitute.For<IToolGate>();
    private readonly ProjectTools _tools;

    public ProjectIdGetToolTests()
    {
        _gate.RequireAsync(Arg.Any<string?>(), Arg.Any<AccessRequirement>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnregisteredProjectException("caller"));
        _gate.WrapAsync(Arg.Any<string?>(), Arg.Any<ProjectTools.ProjectIdGetResult>(), Arg.Any<CancellationToken>())
            .Returns(call => new ApiEnvelope<ProjectTools.ProjectIdGetResult>(
                call.Arg<ProjectTools.ProjectIdGetResult>(), new PromotionMeta(0, null)));
        _tools = new ProjectTools(_registry, _directory, _gate);
    }

    [Fact]
    public async Task OneMatch_ReturnsTheIdAsStored()
    {
        _directory.FindByNameAsync("ai-badger", Arg.Any<CancellationToken>()).Returns(["ai-badger"]);

        var envelope = await _tools.FindByName("ai-badger", TestContext.Current.CancellationToken);

        envelope.Data!.ProjectId.ShouldBe("ai-badger");
    }

    [Fact]
    public async Task NotFound_RefusesAndNeverRegisters()
    {
        _directory.FindByNameAsync("acme", Arg.Any<CancellationToken>()).Returns([]);

        await Should.ThrowAsync<ProjectNotFoundException>(() =>
            _tools.FindByName("acme", TestContext.Current.CancellationToken));

        await _directory.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default, TestContext.Current.CancellationToken);
        await _registry.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoMatches_RefusesAmbiguousListingBoth()
    {
        _directory.FindByNameAsync("acme", Arg.Any<CancellationToken>()).Returns([First, Second]);

        var refusal = await Should.ThrowAsync<ProjectNameAmbiguousException>(() =>
            _tools.FindByName("acme", TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain(First);
        refusal.Message.ShouldContain(Second);
        await _registry.DidNotReceiveWithAnyArgs().RegisterAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TakesNoProjectId_PassesOnEmptyRegistry()
    {
        _directory.FindByNameAsync("acme", Arg.Any<CancellationToken>()).Returns([First]);

        var envelope = await _tools.FindByName("acme", TestContext.Current.CancellationToken);

        envelope.Data!.ProjectId.ShouldBe(First);
        await _gate.Received(1).RequireBankAvailableAsync("project_id_get", Arg.Any<CancellationToken>());
        await _gate.DidNotReceiveWithAnyArgs().RequireAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RefusesWhileAModelMigrationIsOpen()
    {
        _gate.RequireBankAvailableAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelMigrationInProgressException("ai-raccoon: a model migration is in progress"));

        await Should.ThrowAsync<ModelMigrationInProgressException>(() =>
            _tools.FindByName("acme", TestContext.Current.CancellationToken));

        await _directory.DidNotReceiveWithAnyArgs().FindByNameAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Refusals_MapToTheirWirePrefixes()
    {
        ToolRefusals.PrefixFor(new ProjectNotFoundException("acme")).ShouldBe("project-not-found");
        ToolRefusals.PrefixFor(new ProjectNameAmbiguousException("acme", [First, Second])).ShouldBe("project-name-ambiguous");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankName_IsAnInvalidArgument(string name)
    {
        await Should.ThrowAsync<ArgumentException>(() => _tools.FindByName(name, TestContext.Current.CancellationToken));

        await _directory.DidNotReceiveWithAnyArgs().FindByNameAsync(default!, TestContext.Current.CancellationToken);
    }
}
