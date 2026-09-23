using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     P2's proof channel on the real host (ADR-0106 D2): POST /identity/prove answers a challenge
///     with an IEEE-P1363 signature over the frozen transcript, refuses a foreign root without
///     echoing either fingerprint, and is reachable without the token while /mcp and /shutdown
///     stay gated. The relay gate lives here too — it needs a real backend answering on another
///     port.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class IdentityProofChannelTests : IAsyncLifetime
{
    private const string Token = "proof-channel-token-0123456789012345678901234567890";

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-proof-channel");

    private IAsyncDisposable? _envGate;

    /// <summary>
    ///     Holds the env gate as a reader: this class opens a bank through the real host, so an
    ///     encryption test's window would make it open a plain bank with a key (docs/adr/0066).
    /// </summary>
    public async ValueTask InitializeAsync() =>
        _envGate = await TestData.HoldEnvGateAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        if (_envGate is not null)
        {
            await _envGate.DisposeAsync();
        }
    }

    /// <summary>
    ///     The gate: a fresh nonce comes back signed in the frozen P1363 encoding under the key the
    ///     serve side minted into this root's state directory — the primitive the verifier trusts.
    /// </summary>
    [RetryFact]
    public async Task Prove_WithFreshNonce_ReturnsAP1363SignatureVerifyingUnderTheStateDirKey()
    {
        using var key = await MintAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var nonce = IdentityProof.NewNonce();
            var rootFp = IdentityProof.RootFingerprint(_dataRoot);
            var keyId = IdentityProof.KeyId(key);

            using var response = await PostChallengeAsync(port, nonce, rootFp, keyId);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var proof = await ProofBodyAsync(response);
            proof.V.ShouldBe(1);
            proof.KeyId.ShouldBe(keyId);
            Base64Url.DecodeFromChars(proof.Signature).Length.ShouldBe(IdentityProof.SignatureLength);
            IdentityProof.Verify(key, nonce, rootFp, port, proof.KeyId, proof.Signature).ShouldBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     F1/D2: a challenge for another root is refused with no signature, and the refusal body
    ///     echoes neither the foreign fingerprint nor the listener's own (F12 disclosure).
    /// </summary>
    [RetryFact]
    public async Task Prove_RefusesRootMismatch_WithoutEchoingTheRootFingerprint()
    {
        using var key = await MintAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port));
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var foreignRootFp = IdentityProof.RootFingerprint(Path.Combine(_dataRoot, "somewhere-else"));
            var ownRootFp = IdentityProof.RootFingerprint(_dataRoot);
            var keyId = IdentityProof.KeyId(key);

            using var response = await PostChallengeAsync(port, IdentityProof.NewNonce(), foreignRootFp, keyId);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldContain("root-mismatch");
            body.ShouldNotContain(foreignRootFp);
            body.ShouldNotContain(ownRootFp);
            body.ShouldNotContain(keyId);
            body.ShouldNotContain("signature");
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    ///     The allowlist widening opens exactly one path: the pre-token proof answers without a
    ///     token while the two token-bearing routes stay refused.
    /// </summary>
    [RetryFact]
    public async Task Endpoint_ReachableWithoutTheToken_WhileMcpAndShutdownStayGated()
    {
        using var key = await MintAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(port) with { McpToken = Token });
        lease.ReleaseForBind();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var proof = await PostChallengeAsync(port, IdentityProof.NewNonce(),
                IdentityProof.RootFingerprint(_dataRoot), IdentityProof.KeyId(key));

            proof.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var mcp = await Client.PostAsync($"http://127.0.0.1:{port}/mcp",
                new StringContent("x", System.Text.Encoding.UTF8, "application/json"),
                TestContext.Current.CancellationToken);
            mcp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

            using var shutdown = await Client.PostAsync($"http://127.0.0.1:{port}/shutdown", null,
                TestContext.Current.CancellationToken);
            shutdown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private InfrastructureOptions Options => new() { DataRoot = _dataRoot, Scope = InstallScope.User };

    private ServerConfig Config(int port) => new(port, McpTransport.Http, Options);

    private async Task<ECDsa> MintAsync() =>
        await new IdentityKeyFile(Options).EnsureAsync(TestContext.Current.CancellationToken)
        ?? throw new InvalidOperationException("the test's key mint returned nothing");

    private static Task<HttpResponseMessage> PostChallengeAsync(int port, string nonce, string rootFp,
        string keyId) =>
        Client.PostAsJsonAsync($"http://127.0.0.1:{port}{IdentityProof.EndpointPath}",
            new { v = 1, nonce, rootFp, keyId }, TestContext.Current.CancellationToken);

    private static async Task<ProofBody> ProofBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new ProofBody(
            root.GetProperty("v").GetInt32(),
            root.GetProperty("keyId").GetString() ?? "",
            root.GetProperty("signature").GetString() ?? "");
    }

    private sealed record ProofBody(int V, string KeyId, string Signature);
}
