using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using FluentValidation;
using ModelContextProtocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Air-merge P3/P4 share-extract boundary: every element meets the gate (fold on write AND on
///     read once migrated), fragments of one project dedup to a single canonical id, a blank
///     element refuses at the gate, and an empty array still fails the validator.
///     <para>
///         Honesty ledger (mutation : filter : fixture): skip-dedup :
///         --filter ShareExtract_MultiFragment_FoldsToOneCanonicalId : jsaa+loser pair;
///         skip-per-element-gate : --filter ShareExtract_BlankElement_RefusesAtTheGate : [""] element;
///         drop-NotEmpty-rule : --filter ShareExtract_EmptyArray_FailsTheValidator : empty array;
///         reads-do-not-fold : --filter ShareExtract_SingleLoser_ThreadsTheWinner : loser in propose mode.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
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
    public async Task ShareExtract_MultiFragment_FoldsToOneCanonicalId()
    {
        // Ledger — skip-dedup : --filter ShareExtract_MultiFragment_FoldsToOneCanonicalId : jsaa+loser pair, migrated.
        var (service, tools) = NewStack(migrated: true);

        await tools.ShareExtract(["jsaa", "job-search-ai-assistant"],
            cancellationToken: TestContext.Current.CancellationToken);

        service.LastRequest!.ProjectIds.ShouldBe(["jsaa"]);
        service.LastRequest.MetaProjectId.ShouldBe("jsaa",
            "a deduped single project scopes the queue meta instead of reading bank-wide");
    }

    [Fact]
    public async Task ShareExtract_SingleLoser_ThreadsTheWinner()
    {
        // Ledger — reads-do-not-fold : --filter ShareExtract_SingleLoser_ThreadsTheWinner : loser in propose mode, migrated.
        // Propose mode is a READ — this pins that reads fold too once migrated (the continuity
        // half of activation: a cached loser id keeps finding its rows after the repair).
        var (service, tools) = NewStack(migrated: true);

        await tools.ShareExtract(["job-search-ai-assistant"],
            cancellationToken: TestContext.Current.CancellationToken);

        service.LastRequest!.ProjectIds.ShouldBe(["jsaa"]);
    }

    [Fact]
    public async Task ShareExtract_BlankElement_RefusesAtTheGate()
    {
        // Ledger — skip-per-element-gate : --filter ShareExtract_BlankElement_RefusesAtTheGate : [""] element, migrated.
        var (_, tools) = NewStack(migrated: true);

        var ex = await Should.ThrowAsync<McpException>(() =>
            tools.ShareExtract([""], cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith(
            "invalid-params: projectId is required (no registered project's scope contains cwd ");
    }

    [Fact]
    public void ShareExtract_EmptyArray_FailsTheValidator()
    {
        // Ledger — drop-NotEmpty-rule : --filter ShareExtract_EmptyArray_FailsTheValidator : empty array at the validator.
        var validator = new ShareExtractRequestValidator();

        Should.Throw<ValidationException>(() =>
            validator.ValidateAndThrow(new ShareExtractRequest([])));
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
