using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Projects;
using AiRaccoon.Projects;
using AiRaccoon.Tests;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     The gate's blank-projectId branch: a call that names no project consults the injected
///     resolver (and only then — an explicit id never touches it), canonicalizes a resolved id
///     exactly once, and refuses None/Ambiguous with the enriched cwd-aware message. With no
///     resolver wired the enriched None refusal fires unchanged — the wiring, not the message,
///     is what DI adds.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolGateCwdDefaultTests
{
    private readonly RecordingAccessGuard _guard = new();
    private readonly FakePromotionQueue _queue = new();
    private readonly NeverMigratingStore _migrations = new();
    private readonly RecordingRegistrationGuard _registration = new();
    private readonly StubResolver _resolver = new();

    private ToolGate NewGate() =>
        new(_guard, _queue, _migrations, _registration, new NeverMigratedGate(), _resolver);

    [Fact]
    public async Task BlankId_Resolved_NonCanonicalGuid_CanonicalizedOnce()
    {
        var guid = Guid.NewGuid();
        _resolver.Result = new ProjectIdResolution.Resolved($"{{{guid.ToString("D").ToUpperInvariant()}}}");
        var gate = NewGate();

        var canonical = await gate.RequireAsync("", AccessRequirement.Write, "memory_write",
            TestContext.Current.CancellationToken);

        canonical.ShouldBe(guid.ToString("D"));
        // Both guards observe the canonical D-form — the resolved spelling never leaks downstream.
        _guard.Calls.Single().ProjectId.ShouldBe(guid.ToString("D"));
        _registration.Calls.Single().ProjectId.ShouldBe(guid.ToString("D"));
    }

    [Fact]
    public async Task BlankId_None_EnrichedRefusal()
    {
        _resolver.Result = new ProjectIdResolution.None();
        var gate = NewGate();

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync("", AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));

        // Cwd-tolerant: the stable prefix + tail around the probed working directory.
        ex.Message.ShouldStartWith(
            "invalid-params: projectId is required (no registered project's scope contains cwd ");
        ex.Message.ShouldContain(
            "; pass projectId explicitly, or register this directory with memory_watch_add / settings ingest scope add)");
        _guard.Calls.ShouldBeEmpty();
        _registration.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task BlankId_Ambiguous_RefusalListsCandidates_NoCall()
    {
        _resolver.Result = new ProjectIdResolution.Ambiguous(["p1", "p2"]);
        var gate = NewGate();

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync("", AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: projectId is ambiguous from cwd ");
        ex.Message.ShouldContain(": candidates p1, p2");
        _guard.Calls.ShouldBeEmpty();
        _registration.Calls.ShouldBeEmpty();
    }

    /// <summary>
    ///     P4 resolver coherence: a blank id over an alias pair (loser + winner fragments) stays
    ///     Ambiguous — the gate never folds or guesses across fragments on the blank path.
    ///     Ledger — fold-across-fragments : --filter BlankId_AliasPair_StaysAmbiguous :
    ///     loser+winner fragments.
    /// </summary>
    [Fact]
    public async Task BlankId_AliasPair_StaysAmbiguous()
    {
        _resolver.Result = new ProjectIdResolution.Ambiguous(["job-search-ai-assistant", "jsaa"]);
        var gate = NewGate();

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync("", AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith("invalid-params: projectId is ambiguous from cwd ");
        ex.Message.ShouldContain(": candidates job-search-ai-assistant, jsaa");
        _guard.Calls.ShouldBeEmpty();
        _registration.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExplicitId_ResolverNeverConsulted()
    {
        // Load-bearing wiring test: the throwing stub fails the run outright if the resolver is
        // consulted on an explicit id — consulted-and-ignored cannot hide from it.
        var gate = new ToolGate(_guard, _queue, _migrations, _registration, new NeverMigratedGate(), new ThrowingResolver());

        var canonical = await gate.RequireAsync("{ACME}", AccessRequirement.Write, "memory_write",
            TestContext.Current.CancellationToken);

        canonical.ShouldBe("{ACME}");
        _guard.Calls.ShouldBe([("{ACME}", AccessRequirement.Write, "memory_write")]);
    }

    [Fact]
    public async Task BlankId_ResolverOmitted_EnrichedNoneRefusal()
    {
        // The optional ctor param's default: no resolver wired — the enriched None refusal still fires.
        var gate = new ToolGate(_guard, _queue, _migrations, _registration, new NeverMigratedGate());

        var ex = await Should.ThrowAsync<McpException>(() =>
            gate.RequireAsync(null, AccessRequirement.Write, "memory_write", TestContext.Current.CancellationToken));

        ex.Message.ShouldStartWith(
            "invalid-params: projectId is required (no registered project's scope contains cwd ");
        ex.Message.ShouldContain(
            "; pass projectId explicitly, or register this directory with memory_watch_add / settings ingest scope add)");
        _guard.Calls.ShouldBeEmpty();
        _registration.Calls.ShouldBeEmpty();
    }

    private sealed class StubResolver : IProjectIdResolver
    {
        public ProjectIdResolution Result { get; set; } = new ProjectIdResolution.None();

        public Task<ProjectIdResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class ThrowingResolver : IProjectIdResolver
    {
        public Task<ProjectIdResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("resolver consulted on an explicit id");
    }

    private sealed class RecordingAccessGuard : IMemoryAccessGuard
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

    private sealed class RecordingRegistrationGuard : IProjectRegistrationGuard
    {
        public List<(string ProjectId, AccessRequirement Requirement)> Calls { get; } = [];

        public Task EnsureAsync(string projectId, AccessRequirement requirement,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((projectId, requirement));
            return Task.CompletedTask;
        }
    }
}
