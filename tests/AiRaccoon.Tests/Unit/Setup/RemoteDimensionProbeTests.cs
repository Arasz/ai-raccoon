using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     WP4 / plan D10: `model set openai` probes the endpoint for its real output dimension BEFORE
///     the migration outbox commits. Committing first and discovering the mismatch in the drain
///     leaves the bank pending behind a closed ToolGate with nothing able to finish it — the refusal
///     has to happen while nothing has been written.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class RemoteDimensionProbeTests
{
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store,
        IRemoteDimensionProbe probe) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
            new SettingsCommands(probe).ModelSetOpenAiAsync(parsed.ParsedCliArgs, store, store, streams, ct));

    [Fact]
    public async Task DeclaredDimsDisagreeWithTheEndpoint_IsRefused_AndTheOutboxNeverCommits()
    {
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Run(["model", "embedding", "set", "openai", "some-model", "--api-key", "k", "--dims", "1024"], store, Probe(384)));

        ex.Message.ShouldContain("1024");
        ex.Message.ShouldContain("384");
        store.Configured.ShouldBeNull("a refused model set must not mark the bank pending");
        store.Settings.ShouldNotContainKey(EmbeddingSettingsKeys.Dimensions,
            "the declared dimension must not be persisted when the probe contradicts it");
    }

    [Fact]
    public async Task DeclaredDimsMatchTheEndpoint_Commits()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(
            ["model", "embedding", "set", "openai", "some-model", "--api-key", "k", "--dims", "1024"], store, Probe(1024));

        exit.ShouldBe(0);
        store.Configured.ShouldNotBeNull();
        store.Settings[EmbeddingSettingsKeys.Dimensions].ShouldBe("1024");
    }

    /// <summary>
    ///     Undeclared dims used to mean "assume 384". A 3072-dim endpoint would then write 3072-wide
    ///     vectors into a float[384] table, so silence fails closed and names the fix.
    /// </summary>
    [Fact]
    public async Task UndeclaredDims_WithANon384Endpoint_FailsClosed_NamingTheFlag()
    {
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Run(["model", "embedding", "set", "openai", "some-model", "--api-key", "k"], store, Probe(3072)));

        ex.Message.ShouldContain("3072");
        ex.Message.ShouldContain("--dims", customMessage: "the refusal must name the flag that fixes it");
        store.Configured.ShouldBeNull();
    }

    [Fact]
    public async Task UndeclaredDims_WithA384Endpoint_Commits()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["model", "embedding", "set", "openai", "some-model", "--api-key", "k"], store, Probe(384));

        exit.ShouldBe(0);
        store.Configured.ShouldNotBeNull("384 is the legacy shape and needs no declaration");
    }

    /// <summary>An unreachable endpoint must not silently commit a guess.</summary>
    [Fact]
    public async Task ProbeFails_IsRefused_AndSaysWhy()
    {
        var store = new FakeConfigStore();

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            Run(["model", "embedding", "set", "openai", "some-model", "--api-key", "k", "--dims", "1024"], store,
                new ThrowingProbe()));

        ex.Message.ShouldContain("could not be reached");
        store.Configured.ShouldBeNull();
    }

    private static IRemoteDimensionProbe Probe(int dimensions) => new StubProbe(dimensions);

    private sealed class StubProbe(int dimensions) : IRemoteDimensionProbe
    {
        public Task<int> ProbeAsync(string model, string? baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            Task.FromResult(dimensions);
    }

    private sealed class ThrowingProbe : IRemoteDimensionProbe
    {
        public Task<int> ProbeAsync(string model, string? baseUrl, string? apiKey, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
