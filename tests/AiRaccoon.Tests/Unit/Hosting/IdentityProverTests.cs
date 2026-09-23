using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.Hosting;

/// <summary>
///     P2's client half (ADR-0106 D2): the verifier derives a fresh nonce per attempt, reconstructs
///     the transcript with the port it actually dialled, and maps every hostile answer to a
///     not-proven reason. It never mints — a root with no key file is NotProven and gains no files.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IdentityProverTests : IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-identity-prover");

    public void Dispose()
    {
        _client.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    private InfrastructureOptions Options => new() { DataRoot = _dataRoot, Scope = InstallScope.User };

    /// <summary>Positive control for the whole adversarial set: an honest FakeRaccoon is proven.</summary>
    [RetryFact]
    public async Task Prover_RoundTripsAnHonestFakeRaccoon()
    {
        using var key = await MintAsync();
        await using var fake = await StartFakeAsync(Proof(key));

        var failure = await Prover().ProveAsync(McpEndpoint(fake.Port), TestContext.Current.CancellationToken);

        failure.ShouldBeNull();
    }

    /// <summary>
    ///     The gate: a response captured from one successful attempt is replayed against the next.
    ///     A fresh nonce makes the captured signature describe a different transcript.
    /// </summary>
    [RetryFact]
    public async Task Replay_OfACapturedResponse_IsNotProven()
    {
        using var key = await MintAsync();
        await using var fake = await StartFakeAsync(Proof(key));
        var prover = Prover();

        // Attempt 1 proves honestly — the response this attempt captures is a real one.
        (await prover.ProveAsync(McpEndpoint(fake.Port), TestContext.Current.CancellationToken))
            .ShouldBeNull();

        // The listener now echoes the last proof it produced for whatever nonce it is sent.
        fake.ReplayLastProof = true;

        (await prover.ProveAsync(McpEndpoint(fake.Port), TestContext.Current.CancellationToken))
            .ShouldBe(IdentityProofFailure.BadSignature);

        var nonces = fake.ProofNonces;
        nonces.Count.ShouldBe(2);
        nonces.Distinct(StringComparer.Ordinal).Count().ShouldBe(2,
            "a reused nonce is exactly what would let the captured response verify");
    }

    /// <summary>F39-adjacent: the verifier is read-only, so an empty root stays empty.</summary>
    [RetryFact]
    public async Task Prover_WithNoKeyFile_IsNotProven_AndCreatesZeroFiles()
    {
        Directory.GetFileSystemEntries(_dataRoot).ShouldBeEmpty();

        var failure = await Prover().ProveAsync(new Uri("http://127.0.0.1:1/mcp"),
            TestContext.Current.CancellationToken);

        failure.ShouldBe(IdentityProofFailure.NoKey);
        Directory.GetFileSystemEntries(_dataRoot).ShouldBeEmpty();
    }

    /// <summary>
    ///     Garbage, a DER signature (a valid ECDSA encoding, not this wire's), an oversized body and
    ///     a hanging listener all end NotProven — the slow one inside the verifier's own budget.
    /// </summary>
    [RetryFact]
    public async Task Verifier_RejectsMalformed_Oversized_AndSlowResponses_WithinBudget()
    {
        using var key = await MintAsync();

        await using (var garbage = await StartFakeAsync(Proof(key) with { RawResponse = "not-a-proof" }))
        {
            (await Prover().ProveAsync(McpEndpoint(garbage.Port), TestContext.Current.CancellationToken))
                .ShouldBe(IdentityProofFailure.Malformed);
        }

        await using (var der = await StartFakeAsync(Proof(key) with { DerSignature = true }))
        {
            (await Prover().ProveAsync(McpEndpoint(der.Port), TestContext.Current.CancellationToken))
                .ShouldBe(IdentityProofFailure.Malformed);
        }

        await using (var oversized = await StartFakeAsync(Proof(key) with { RawResponse = new string('x', 64 * 1024) }))
        {
            (await Prover().ProveAsync(McpEndpoint(oversized.Port), TestContext.Current.CancellationToken))
                .ShouldBe(IdentityProofFailure.Malformed);
        }

        await using (var slow = await StartFakeAsync(Proof(key) with { Delay = TimeSpan.FromSeconds(10) }))
        {
            var budget = TimeSpan.FromSeconds(1);
            var started = Stopwatch.GetTimestamp();

            var failure = await new IdentityProver(Options, _client, budget)
                .ProveAsync(McpEndpoint(slow.Port), TestContext.Current.CancellationToken);

            failure.ShouldBe(IdentityProofFailure.Timeout);
            Stopwatch.GetElapsedTime(started).ShouldBeLessThan(budget + TimeSpan.FromSeconds(1),
                "the verifier must give up inside its own budget, not the listener's");
        }
    }

    private static Uri McpEndpoint(int port) => new($"http://127.0.0.1:{port}/mcp");

    private IdentityProver Prover() => new(Options, _client);

    private FakeRaccoonProof Proof(ECDsa key) =>
        new() { Signer = key, RootFp = IdentityProof.RootFingerprint(_dataRoot) };

    private static async Task<FakeRaccoon> StartFakeAsync(FakeRaccoonProof proof)
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        return await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken, proof: proof);
    }

    private async Task<ECDsa> MintAsync() =>
        await new IdentityKeyFile(Options).EnsureAsync(TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException("the test's key mint returned nothing");
}
