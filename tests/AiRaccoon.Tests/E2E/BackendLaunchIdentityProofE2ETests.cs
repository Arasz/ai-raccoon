using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Logging;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     ADR-0106 end to end, on the built binary at both ends: the real proxy and the real
///     `serve --restart` verify, the real `serve` proves, so the crypto round trip here is the
///     product's own and not a fixture echo. Every hostile listener is shown to have received zero
///     secret bytes, and only the probe and the bounded challenge; every cell names the defence
///     that stopped it, so a cell cannot pass because the attack never reached the proof.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
[UnsupportedOSPlatform("windows")]
public sealed partial class BackendLaunchIdentityProofE2ETests : IAsyncLifetime
{
    /// <summary>Who holds the port the client is about to trust.</summary>
    public enum Attacker
    {
        /// <summary>The real server of this root — the positive control of every path.</summary>
        Honest,

        /// <summary>The F70 squatter, now also claiming the ai-raccoon name: answers the challenge with junk.</summary>
        Squatter,

        /// <summary>Forwards each challenge verbatim to a real same-root server on another port and returns its genuine signature.</summary>
        RelaySameRootOtherPort,

        /// <summary>Replays a genuine response the real server gave on this very port for an earlier nonce.</summary>
        Replay,

        /// <summary>Plants its own key as the root's identity-key, readable by others, and signs correctly with it.</summary>
        PlantedKey,

        /// <summary>A real server on another root whose state directory is a copy of this one's: same key, same token.</summary>
        CrossRootCopy
    }

    /// <summary>The moments a client hands over the token (ADR-0106: prove before every one).</summary>
    public enum LaunchPath
    {
        /// <summary>The proxy's acquire of the configured port.</summary>
        Acquire,

        /// <summary>A bare `serve --restart` asking the configured port's holder to stop.</summary>
        Restart,

        /// <summary>The proxy stopping its private fallback at exit, after its port changed hands.</summary>
        Dispose
    }

    /// <summary>Only stops a hang from wedging the run.</summary>
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(120);

    private static readonly UnixFileMode SharedReadable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private readonly List<string> _roots = [];
    private IAsyncDisposable? _env;
    private InfrastructureOptions _options = null!;
    private string[] _secrets = [];

    public static TheoryData<Attacker, LaunchPath> Cells()
    {
        var cells = new TheoryData<Attacker, LaunchPath>();
        foreach (var path in Enum.GetValues<LaunchPath>())
        {
            foreach (var attacker in Enum.GetValues<Attacker>())
            {
                cells.Add(attacker, path);
            }
        }

        return cells;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateDirectory => BankPaths.DirectoryFor(_options);

    private string QuietLog => QuietLogging.LogFilePath(_options);

    public async ValueTask InitializeAsync()
    {
        // Every process here opens a plain bank; a passphrase left in the environment would make them key it.
        _env = await EnvScope.AcquireAsync(Ct, (EnvEncryptionKeyProvider.EnvVarName, null));
        _options = TestData.CreateProjectOptions(NewRoot("identity-proof-a"));
        await TestData.SeedBankAsync(_options, Ct);
        // What a first `serve` leaves behind: the token and the identity key in the state directory.
        var token = await new McpTokenFile(_options).EnsureAsync(Ct)
                    ?? throw new InvalidOperationException("the fixture could not mint its token");
        (await new IdentityKeyFile(_options).EnsureAsync(Ct)).ShouldNotBeNull();
        _secrets = SecretsOf(token, Path.Combine(StateDirectory, IdentityKeyFile.FileName));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var root in _roots)
        {
            TestData.DeleteTempRoot(root);
        }

        if (_env is not null)
        {
            await _env.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(Cells))]
    public async Task ThreatMatrix_FiveAttackers_ThreePaths_ZeroSecretBytesEverywhere(Attacker attacker, LaunchPath path)
    {
        switch (path)
        {
            case LaunchPath.Acquire:
                await AcquireCellAsync(attacker);
                break;
            case LaunchPath.Restart:
                await RestartCellAsync(attacker);
                break;
            case LaunchPath.Dispose:
                await DisposeCellAsync(attacker);
                break;
        }
    }

    /// <summary>
    ///     The dispose path under a stateful client: the proxy holds MCP sessions on its private
    ///     children, so their shutdown has more to send than the stop. Every child dies and a racer
    ///     takes each port; each racer still gets the challenge alone — no session close, no stop,
    ///     no token — and each child is reported as not proven.
    /// </summary>
    [Fact]
    public async Task DisposeStop_UnderAStatefulClient_EveryRacerOnADeadChildsPortGetsOnlyTheChallenge()
    {
        var configured = FreePort();
        using var squatter = new Impostor(configured, _ => Task.FromResult(new ImpostorReply(200, "{}")));
        await using var proxy = StartProxy(configured, ProxyProcess.Stateful);
        (await proxy.ListToolsAsync(Ct)).ShouldNotBeEmpty();
        var childPorts = ChildPorts();
        childPorts.ShouldNotBeEmpty();

        var racers = new List<Impostor>();
        try
        {
            foreach (var childPort in childPorts)
            {
                var pid = await RealServe.PidOnAsync(childPort, Ct)
                          ?? throw new InvalidOperationException($"nothing answers on the child's port {childPort}");
                using (var child = Process.GetProcessById(pid))
                {
                    (await RaccoonProcess.KillTreeAndWaitAsync(child, HardCap, Ct)).ShouldBeTrue();
                }

                racers.Add(new Impostor(childPort, _ => Task.FromResult(new ImpostorReply(200, "{}"))));
            }

            (await proxy.CloseAsync(HardCap)).ShouldBe(ExitCode.Success, proxy.Stderr);

            foreach (var racer in racers)
            {
                proxy.Stderr.ShouldContain(
                    $"the private backend at http://127.0.0.1:{racer.Port}/mcp no longer proves it serves this data root ({nameof(IdentityProofFailure.Malformed)})");
                racer.Challenges.ShouldBeGreaterThan(0, "the stop must have reached the proof");
                AssertNothingSecret(racer, LaunchPath.Dispose);
            }

            AssertNothingSecret(squatter, LaunchPath.Acquire);
        }
        finally
        {
            foreach (var racer in racers)
            {
                racer.Dispose();
            }
        }
    }

    private async Task AcquireCellAsync(Attacker attacker)
    {
        var port = FreePort();
        var captured = attacker is Attacker.Replay ? await CaptureFromAFreshServerAsync(port) : null;
        await using var attack = await MountAsync(attacker, port, captured);
        await using var proxy = StartProxy(port);

        Exception? failure = null;
        try
        {
            (await proxy.ListToolsAsync(Ct)).ShouldNotBeEmpty();
        }
        catch (IOException ex)
        {
            failure = ex;
        }

        var exit = await proxy.CloseAsync(HardCap);
        if (attacker is Attacker.Honest)
        {
            failure.ShouldBeNull(failure?.ToString());
            exit.ShouldBe(ExitCode.Success, proxy.Stderr);
            proxy.Stderr.ShouldNotContain("did not prove it serves this data root");
            ChildPorts().ShouldBeEmpty("a proven listener is attached to; nothing may be spawned");
            (await RealServe.PidOnAsync(port, Ct)).ShouldBe(attack.Server!.Process.Id);
            return;
        }

        proxy.Stderr.ShouldContain(
            $"the listener on port {port} did not prove it serves this data root ({ExpectedReason(attacker, LaunchPath.Acquire)})");
        if (attacker is Attacker.PlantedKey)
        {
            // The fallback `serve` refuses the shared key too, so there is no backend at all — and still no secret.
            failure.ShouldNotBeNull();
            exit.ShouldBe(ExitCode.ProxyBackendUnavailable, proxy.Stderr);
            proxy.Stderr.ShouldContain("no private backend could be started");
        }
        else
        {
            failure.ShouldBeNull(failure?.ToString());
            exit.ShouldBe(ExitCode.Success, proxy.Stderr);
        }

        AssertTheAttackGotNothing(attack, attacker, LaunchPath.Acquire);
    }

    private async Task RestartCellAsync(Attacker attacker)
    {
        var port = FreePort();
        var captured = attacker is Attacker.Replay ? await CaptureFromAFreshServerAsync(port) : null;
        await using var attack = await MountAsync(attacker, port, captured);
        string[] restart = ["serve", "--port", port.ToString(CultureInfo.InvariantCulture), "--restart"];

        if (attacker is Attacker.Honest)
        {
            var old = attack.Server!.Process;
            await using var cycled = await RealServe.StartAsync(_options, restart, port, Ct);
            await old.WaitForExitAsync(Ct).WaitAsync(HardCap, Ct);
            old.ExitCode.ShouldBe(ExitCode.Success);
            (await RealServe.PidOnAsync(port, Ct)).ShouldBe(cycled.Process.Id);
            return;
        }

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _options.DataRoot, "--install-scope", "project", .. restart], HardCap, Ct);

        run.ExitCode.ShouldBe(ExitCode.PortInUse, run.Stderr);
        run.Stderr.ShouldContain($"did not prove it holds this data root's identity key ({ExpectedReason(attacker, LaunchPath.Restart)}); nothing is asked to stop");
        run.Stderr.ShouldContain("stop the listener yourself");
        AssertTheAttackGotNothing(attack, attacker, LaunchPath.Restart);
    }

    private async Task DisposeCellAsync(Attacker attacker)
    {
        // An unproven listener on the configured port sends the proxy to a private fallback child.
        var configured = FreePort();
        using var squatter = new Impostor(configured, _ => Task.FromResult(new ImpostorReply(200, "{}")));
        await using var proxy = StartProxy(configured);
        Attack? attack = null;
        try
        {
            (await proxy.ListToolsAsync(Ct)).ShouldNotBeEmpty();
            var childPort = ChildPorts().ShouldHaveSingleItem("the proxy must have started exactly one fallback child");
            var childPid = await RealServe.PidOnAsync(childPort, Ct)
                           ?? throw new InvalidOperationException($"nothing answers on the child's port {childPort}");

            if (attacker is Attacker.Honest)
            {
                (await proxy.CloseAsync(HardCap)).ShouldBe(ExitCode.Success, proxy.Stderr);
                (await WaitUntilGoneAsync(childPid)).ShouldBeTrue("the proven child must be stopped with the proxy");
                ReadQuietLog().ShouldContain($"shutdown requested over /shutdown; stopping pid {childPid}");
                proxy.Stderr.ShouldNotContain("no longer proves");
                return;
            }

            var captured = attacker is Attacker.Replay ? await ChallengeAsync(childPort) : null;
            // The child dies and its port changes hands before the proxy gets round to stopping it.
            using (var child = Process.GetProcessById(childPid))
            {
                (await RaccoonProcess.KillTreeAndWaitAsync(child, HardCap, Ct)).ShouldBeTrue();
            }

            attack = await MountAsync(attacker, childPort, captured);
            (await proxy.CloseAsync(HardCap)).ShouldBe(ExitCode.Success, proxy.Stderr);

            proxy.Stderr.ShouldContain(
                $"the private backend at http://127.0.0.1:{childPort}/mcp no longer proves it serves this data root ({ExpectedReason(attacker, LaunchPath.Dispose)})");
            AssertTheAttackGotNothing(attack, attacker, LaunchPath.Dispose);
            AssertNothingSecret(squatter, LaunchPath.Acquire);
        }
        finally
        {
            if (attack is not null)
            {
                await attack.DisposeAsync();
            }
        }
    }

    /// <summary>The failure each defence reports: which check stopped the attack on that path.</summary>
    private static string ExpectedReason(Attacker attacker, LaunchPath path) => attacker switch
    {
        Attacker.Squatter => nameof(IdentityProofFailure.Malformed),
        Attacker.RelaySameRootOtherPort => nameof(IdentityProofFailure.BadSignature),
        Attacker.Replay => nameof(IdentityProofFailure.BadSignature),
        // A key others can read is no trust anchor: refused before any challenge is sent. At dispose
        // the verifier already holds the key it read at acquire, so the planted one fails the pin.
        Attacker.PlantedKey => path is LaunchPath.Dispose
            ? nameof(IdentityProofFailure.BadSignature)
            : nameof(IdentityProofFailure.NoKey),
        Attacker.CrossRootCopy => nameof(IdentityProofFailure.RootMismatch),
        _ => throw new ArgumentOutOfRangeException(nameof(attacker), attacker, null)
    };

    private void AssertTheAttackGotNothing(Attack attack, Attacker attacker, LaunchPath path)
    {
        if (attack.Listener is { } listener)
        {
            AssertNothingSecret(listener, path);
            if (attacker is Attacker.PlantedKey && path is not LaunchPath.Dispose)
            {
                listener.Challenges.ShouldBe(0, "a key file others can read is refused before any challenge goes out");
            }
            else
            {
                listener.Challenges.ShouldBeGreaterThan(0, "the attack must have reached the proof, or this cell proves nothing");
            }
        }

        if (attacker is Attacker.RelaySameRootOtherPort)
        {
            // The relay really did obtain genuine same-root signatures: only the port binding stopped it.
            attack.Downstream.ShouldContain(reply => reply.Status == 200);
        }

        if (attacker is Attacker.CrossRootCopy)
        {
            // Same key, same token: any token-bearing request would have served or stopped it.
            attack.Server!.Process.HasExited.ShouldBeFalse("the copied-root server must never be asked to stop");
        }
    }

    /// <summary>Zero secret bytes, and nothing but the probe and the challenge (plus restart's identify read).</summary>
    private void AssertNothingSecret(Impostor listener, LaunchPath path)
    {
        listener.SecretsSeen(_secrets).ShouldBeEmpty();
        listener.Requests.ShouldNotContain(request => request.CarriesTheTokenHeader);
        foreach (var request in listener.Requests)
        {
            var allowed = request is { Method: "POST", Path: "/mcp", BodyText: "x" }
                          || request is { Method: "POST", Path: IdentityProof.EndpointPath }
                          || (path is LaunchPath.Restart && request is { Method: "GET", Path: "/observability" });
            allowed.ShouldBeTrue($"an unproven listener received {request}");
        }
    }

    private async Task<Attack> MountAsync(Attacker attacker, int port, ImpostorReply? captured)
    {
        var attack = new Attack();
        switch (attacker)
        {
            case Attacker.Honest:
                attack.Server = await RealServe.StartAsync(_options, port, Ct);
                break;
            case Attacker.Squatter:
                attack.Listener = new Impostor(port, _ => Task.FromResult(new ImpostorReply(200, "{}")));
                break;
            case Attacker.RelaySameRootOtherPort:
                var target = await RealServe.StartAsync(_options, FreePort(), Ct);
                attack.Owned.Add(target);
                attack.Listener = new Impostor(port, challenge => RelayAsync(target.Port, challenge, attack.Downstream));
                break;
            case Attacker.Replay:
                var replayed = captured ?? throw new InvalidOperationException("a replay needs a captured response");
                attack.Listener = new Impostor(port, _ => Task.FromResult(replayed));
                break;
            case Attacker.PlantedKey:
                var planted = PlantAKeyOthersCanRead();
                attack.Owned.Add(new DisposableKey(planted));
                attack.Listener = new Impostor(port, challenge => Task.FromResult(SignWith(planted, challenge, port)));
                break;
            case Attacker.CrossRootCopy:
                attack.Server = await RealServe.StartAsync(await CopyTheStateDirectoryToAnotherRootAsync(), port, Ct);
                break;
        }

        return attack;
    }

    /// <summary>A genuine answer from a real server of this root on <paramref name="port"/>, then that server stops.</summary>
    private async Task<ImpostorReply> CaptureFromAFreshServerAsync(int port)
    {
        await using var genuine = await RealServe.StartAsync(_options, port, Ct);
        return await ChallengeAsync(port);
    }

    /// <summary>What an eavesdropper keeps: a challenge/answer pair that verifies for this port and its own nonce.</summary>
    private async Task<ImpostorReply> ChallengeAsync(int port)
    {
        using var key = new IdentityKeyFile(_options).Read() ?? throw new InvalidOperationException("no key to challenge with");
        var nonce = IdentityProof.NewNonce();
        var rootFp = IdentityProof.RootFingerprint(StateDirectory);
        var reply = await PostChallengeAsync(port,
            JsonSerializer.Serialize(new { v = 1, nonce, rootFp, keyId = IdentityProof.KeyId(key) }));

        reply.Status.ShouldBe(200, reply.Body);
        using var answer = JsonDocument.Parse(reply.Body);
        IdentityProof.Verify(key, nonce, rootFp, port, answer.RootElement.GetProperty("keyId").GetString(),
            answer.RootElement.GetProperty("signature").GetString()).ShouldBeNull("the captured answer must be genuine for its own nonce");
        return reply;
    }

    private static async Task<ImpostorReply> RelayAsync(int port, string challenge, ConcurrentQueue<ImpostorReply> downstream)
    {
        var reply = await PostChallengeAsync(port, challenge);
        downstream.Enqueue(reply);
        return reply;
    }

    private static async Task<ImpostorReply> PostChallengeAsync(int port, string body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var response = await http.PostAsync(
            $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{IdentityProof.EndpointPath}", content);
        return new ImpostorReply((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Replaces this root's identity-key with an attacker key, mode 0644 — a key anyone can read.</summary>
    private ECDsa PlantAKeyOthersCanRead()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var path = Path.Combine(StateDirectory, IdentityKeyFile.FileName);
        File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
        File.SetUnixFileMode(path, SharedReadable);
        return key;
    }

    /// <summary>A correct D2 answer under <paramref name="key"/>, built with fixture-local crypto from the frozen spec.</summary>
    private static ImpostorReply SignWith(ECDsa key, string challenge, int port)
    {
        using var document = JsonDocument.Parse(challenge);
        var nonce = document.RootElement.GetProperty("nonce").GetString();
        var rootFp = document.RootElement.GetProperty("rootFp").GetString();
        var keyId = Base64Url.EncodeToString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        var transcript = Encoding.UTF8.GetBytes(
            $"ai-raccoon/identity/v1\n{nonce}\n{keyId}\n{rootFp}\n{port.ToString(CultureInfo.InvariantCulture)}");
        var signature = key.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new ImpostorReply(200,
            JsonSerializer.Serialize(new { v = 1, keyId, signature = Base64Url.EncodeToString(signature) }));
    }

    /// <summary>A restored backup at another root: a fresh bank there, and this root's key and token copied in.</summary>
    private async Task<InfrastructureOptions> CopyTheStateDirectoryToAnotherRootAsync()
    {
        var copy = TestData.CreateProjectOptions(NewRoot("identity-proof-copy"));
        await TestData.SeedBankAsync(copy, Ct);
        var target = BankPaths.DirectoryFor(copy);
        foreach (var file in new[] { IdentityKeyFile.FileName, McpTokenFile.FileName })
        {
            File.Copy(Path.Combine(StateDirectory, file), Path.Combine(target, file), overwrite: true);
            File.SetUnixFileMode(Path.Combine(target, file), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return copy;
    }

    /// <summary>The bare proxy on this root, quiet so it and every child it starts log to the state directory.</summary>
    private ProxyProcess StartProxy(int port, string revision = ProxyProcess.Stateless) =>
        ProxyProcess.Start(
        [
            "--data-root", _options.DataRoot, "--install-scope", "project", "--quiet",
            "--port", port.ToString(CultureInfo.InvariantCulture)
        ], revision);

    private string ReadQuietLog()
    {
        if (!File.Exists(QuietLog))
        {
            return "";
        }

        using var stream = new FileStream(QuietLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Every private backend the proxy's launcher reported live, by port.</summary>
    private int[] ChildPorts() =>
    [
        .. BackendLiveLine().Matches(ReadQuietLog())
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
    ];

    [GeneratedRegex(@"backend live at http://127\.0\.0\.1:(\d+)/mcp")]
    private static partial Regex BackendLiveLine();

    private static async Task<bool> WaitUntilGoneAsync(int pid)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(Ct).WaitAsync(HardCap, Ct);
                return true;
            }
            catch (TimeoutException)
            {
                RaccoonProcess.KillTree(process);
                return false;
            }
        }
    }

    private static string[] SecretsOf(string token, string keyPath)
    {
        var pem = File.ReadAllText(keyPath);
        using var key = ECDsa.Create();
        key.ImportFromPem(pem);
        var scalar = key.ExportParameters(true).D!;
        return
        [
            token,
            "PRIVATE KEY",
            .. pem.Split('\n').Select(line => line.Trim()).Where(line => line.Length >= 16 && !line.StartsWith('-')),
            Convert.ToBase64String(scalar),
            Base64Url.EncodeToString(scalar),
            Convert.ToHexString(scalar),
            Convert.ToHexStringLower(scalar)
        ];
    }

    private string NewRoot(string prefix)
    {
        var root = TestData.CreateTempRoot(prefix);
        _roots.Add(root);
        return root;
    }

    private static int FreePort()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        return port;
    }

    /// <summary>One cell's hostile side: the listener or server on the port, and whatever it needs alive.</summary>
    private sealed class Attack : IAsyncDisposable
    {
        public Impostor? Listener { get; set; }

        public RealServe? Server { get; set; }

        public List<IAsyncDisposable> Owned { get; } = [];

        public ConcurrentQueue<ImpostorReply> Downstream { get; } = new();

        public async ValueTask DisposeAsync()
        {
            Listener?.Dispose();
            if (Server is not null)
            {
                await Server.DisposeAsync();
            }

            foreach (var owned in Owned)
            {
                await owned.DisposeAsync();
            }
        }
    }

    private sealed class DisposableKey(ECDsa key) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            key.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
