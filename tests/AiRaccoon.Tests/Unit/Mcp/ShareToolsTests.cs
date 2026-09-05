using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tests;
using AiRaccoon.Tools;
using FluentValidation;
using ModelContextProtocol;
using NSubstitute;
using Shouldly;
using AiRaccoon.Tests.Unit.Projects;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Air-merge P3/P4 share-extract boundary: every element meets the gate (fold on write AND on
///     read once migrated), fragments of one project dedup to a single canonical id, a blank
///     element refuses before the gate (d-425 MUST-2: never cwd-guessed), and an empty array
///     still fails the validator.
///     <para>
///         Honesty ledger (mutation : filter : fixture): skip-dedup :
///         --filter ShareExtract_MultiFragment_StaysTwoProjects : jsaa+loser pair;
///         cwd-guess-on-blank-element : --filter ShareExtract_BlankElement_RefusesBeforeTheGate : [""]/whitespace element;
///         null-coalesces-to-empty : --filter ShareExtract_NullProjectIds_FailsTheValidator_NotTheGate : null array;
///         drop-NotEmpty-rule : --filter ShareExtract_EmptyArray_FailsTheValidator : empty array;
///         drop-blank-element-rule : --filter ShareExtract_BlankElement_FailsTheValidator : blank element at the validator;
///         reads-do-not-fold : --filter ShareExtract_SingleLoser_ThreadsTheNamedId : loser in propose mode.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ShareToolsTests
{
    private static (RecordingShareExtractService Service, ShareTools Tools) NewStack(bool migrated)
    {
        var service = new RecordingShareExtractService();
        var gate = new ToolGate(new AllowingGuard(), new FakePromotionQueue(), new NeverMigratingStore(),
            new AllowingRegistrationGuard(), migrationGate: new StubMigrationGate(migrated));
        return (service, new ShareTools(Substitute.For<IMemoryStore>(), gate, service));
    }

    [Fact]
    public async Task ShareExtract_MultiFragment_StaysTwoProjects()
    {
        // Ledger — skip-dedup : --filter ShareExtract_MultiFragment_StaysTwoProjects : jsaa+loser pair, migrated.
        var (service, tools) = NewStack(migrated: true);

        await tools.ShareExtract(["jsaa", "job-search-ai-assistant"],
            cancellationToken: TestContext.Current.CancellationToken);

        service.LastRequest!.ProjectIds.ShouldBe(["jsaa", "job-search-ai-assistant"],
            "ADR-0099: with the empty default two spellings are two projects — no dedup");
        service.LastRequest.MetaProjectId.ShouldBeNull("two projects read bank-wide meta");
    }

    [Fact]
    public async Task ShareExtract_SingleLoser_ThreadsTheNamedId()
    {
        // Ledger — reads-do-not-fold : --filter ShareExtract_SingleLoser_ThreadsTheNamedId : loser in propose mode, migrated.
        // ADR-0099: the gate passes ids through even when migrated — the caller-named id
        // threads to the runner unchanged.
        var (service, tools) = NewStack(migrated: true);

        await tools.ShareExtract(["job-search-ai-assistant"],
            cancellationToken: TestContext.Current.CancellationToken);

        service.LastRequest!.ProjectIds.ShouldBe(["job-search-ai-assistant"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ShareExtract_BlankElement_RefusesBeforeTheGate(string blank)
    {
        // Ledger — cwd-guess-on-blank-element : --filter ShareExtract_BlankElement_RefusesBeforeTheGate : [""]/whitepace element, migrated.
        // A blank element is refused with its own invalid-params BEFORE the gate: reaching the
        // gate would cwd-guess a project the caller never named (the old test pinned that exact
        // refusal — inverted by d-425 MUST-2). The recording guard proves the gate never ran.
        var guard = new RecordingGuard();
        var service = new RecordingShareExtractService();
        var gate = new ToolGate(guard, new FakePromotionQueue(), new NeverMigratingStore(),
            new AllowingRegistrationGuard(), migrationGate: new StubMigrationGate(true));
        var tools = new ShareTools(Substitute.For<IMemoryStore>(), gate, service);

        var ex = await Should.ThrowAsync<McpException>(() =>
            tools.ShareExtract([blank], cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: projectIds[0]");
        guard.Calls.ShouldBeEmpty("a blank element must never reach the access check");
        service.LastRequest.ShouldBeNull();
    }

    [Fact]
    public async Task ShareExtract_NullProjectIds_FailsTheValidator_NotTheGate()
    {
        // Ledger — null-coalesces-to-empty : --filter ShareExtract_NullProjectIds_FailsTheValidator_NotTheGate : null array, real service.
        // The ""-vs-null contrast: null coalesces to [] and fails the request validator
        // (ValidationException, 1..8 rule) while [""] fails the tool pre-check above
        // (McpException, blank-element rule) — never the twain, never the gate.
        var service = new ShareExtractService(Substitute.For<IMemoryStore>(),
            Substitute.For<ISharedExtractionRunner>(), new FakePromotionQueue());
        var gate = new ToolGate(new AllowingGuard(), new FakePromotionQueue(), new NeverMigratingStore(),
            new AllowingRegistrationGuard(), migrationGate: new StubMigrationGate(true));
        var tools = new ShareTools(Substitute.For<IMemoryStore>(), gate, service);

        var ex = await Should.ThrowAsync<ValidationException>(() =>
            tools.ShareExtract(null!, cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("projectIds must contain 1..8 project ids");
    }

    [Fact]
    public void ShareExtract_EmptyArray_FailsTheValidator()
    {
        // Ledger — drop-NotEmpty-rule : --filter ShareExtract_EmptyArray_FailsTheValidator : empty array at the validator.
        var validator = new ShareExtractRequestValidator();

        Should.Throw<ValidationException>(() =>
            validator.ValidateAndThrow(new ShareExtractRequest([])));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShareExtract_BlankElement_FailsTheValidator(string blank)
    {
        // Ledger — drop-blank-element-rule : --filter ShareExtract_BlankElement_FailsTheValidator : blank element at the validator.
        // The service path (reachable without MCP per IShareExtractService) refuses blanks at
        // validation, so a direct caller gets the same answer as the tool pre-check above.
        var validator = new ShareExtractRequestValidator();

        var ex = Should.Throw<ValidationException>(() =>
            validator.ValidateAndThrow(new ShareExtractRequest([blank])));

        ex.Message.ShouldContain("projectIds");
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

    private sealed class AllowingGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }

    /// <summary>Records the rebuilt request the tool actually handed over — the seam the per-element gate feeds.</summary>
    private sealed class RecordingShareExtractService : IShareExtractService
    {
        public ShareExtractRequest? LastRequest { get; private set; }

        public Task<ShareExtractResult> RunAsync(ShareExtractRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ShareExtractResult([], []));
        }
    }
}
