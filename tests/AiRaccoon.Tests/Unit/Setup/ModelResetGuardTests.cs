using System.Net;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Settings;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     The CLI half of the #592 reset guard (Fast lane): the refusal exits 25 with the frozen
///     message on stderr verbatim (no doubled prefix), `model embedding set` while a migration is
///     open keeps today's exit 15, and <see cref="ServerSettingsStore.DeleteSettingAsync" /> maps a
///     409 body to <see cref="ModelMigrationInProgressException" /> carrying the reason.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ModelResetGuardTests
{
    internal const string FrozenResetRefusalMessage =
        "ai-raccoon: model reset refused: a model migration is in progress — every MCP tool call is refused until it finishes; nothing was deleted";

    /// <summary>
    ///     RED today: the generic catch in <see cref="ConfigCommands" /> exits 15 with the message
    ///     doubled by <c>CliFailureFormatting</c> ("ai-raccoon: ai-raccoon: …").
    /// </summary>
    [Fact]
    public async Task ModelReset_StoreThrowsModelMigrationInProgress_Exits25_WithTheUnprefixedMessage()
    {
        var commands = TestData.CreateConfigCommands(new ThrowingResetStore(FrozenResetRefusalMessage),
            settings: new SettingsCommands());

        var (exit, @out, err) = await CliRun.RunAsync(["settings", "model", "reset"], commands);

        // Literal 25 at RED time; ExitCode.ModelResetRefused lands in the same commit (R2 F1).
        exit.ShouldBe(25);
        err.ShouldBe(FrozenResetRefusalMessage + Environment.NewLine);
        @out.ShouldNotContain("embedding engine reset to default: no engine");
    }

    /// <summary>
    ///     The direct client mapping (precedent <c>SettingsEndpointTests.ServerSettingsStore_ActivateCodeEngine_…</c>):
    ///     the 409 body must surface as <see cref="ModelMigrationInProgressException" /> carrying the
    ///     frozen message — not a bare <c>HttpRequestException</c>.
    /// </summary>
    [Fact]
    public async Task ServerSettingsStore_DeleteSetting_OnConflict_ThrowsModelMigrationInProgressWithTheReason()
    {
        var store = new ServerSettingsStore(
            new HttpClient(new ConflictHandler(FrozenResetRefusalMessage))
            {
                BaseAddress = new Uri("http://127.0.0.1:1/")
            },
            "test-token");

        var ex = await Should.ThrowAsync<ModelMigrationInProgressException>(
            () => store.DeleteSettingAsync(EmbeddingSettingsKeys.Provider));

        ex.Message.ShouldBe(FrozenResetRefusalMessage);
    }

    /// <summary>
    ///     R2 F3: the A2 catch-placement ruling — the catch lives inside
    ///     <see cref="SettingsCommands.ModelResetAsync" />, so `model embedding set` while a
    ///     migration is open keeps today's generic exit 15; a dispatcher-level catch would silently
    ///     change it to 25 (out of scope).
    /// </summary>
    [Fact]
    public async Task ModelEmbeddingSet_WhileMigrationOpen_StillExitsInvalidArgument()
    {
        var reason =
            "ai-raccoon: a model migration is already in progress — every MCP tool call is refused until it finishes";
        var commands = TestData.CreateConfigCommands(new FakeConfigStore(), settings: new SettingsCommands(),
            modelMigrations: new ThrowingMigrationStore(new ModelMigrationInProgressException(reason)));

        var (exit, _, err) = await CliRun.RunAsync(["model", "embedding", "set", "local"], commands);

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain(reason);
    }

    private sealed class ThrowingResetStore(string message) : FakeMemoryStore
    {
        public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            throw new ModelMigrationInProgressException(message);
    }

    private sealed class ThrowingMigrationStore(Exception toThrow) : IModelMigrationStore
    {
        public Task<EmbeddingConfig> StartModelMigrationAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw toThrow;

        public Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class ConflictHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(body) });
    }
}
