using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     EncryptionKeyResolver: picks the key provider from the memory.db.source sidecar, read
///     fresh on every call — absent sidecar or "env" → env provider; "bitwarden" → bws fetch +
///     derivation; corrupt sidecar → loud error.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EncryptionKeyResolverTests : IDisposable
{
    // pinned vector: seed 00 01 … 1e 1f → x'72d2…'
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";

    private readonly string _dataRoot = TestData.CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private EncryptionKeyResolver Resolver(IEncryptionKeyProvider env, FakeBwsRunner runner) => new(new EncryptionSourceSidecar(BankPath()), [env, new BitwardenEncryptionKeyProvider(runner)]);

    private void WriteSidecar(string json) => File.WriteAllText(SidecarPath(), json);

    [Fact]
    public async Task ResolveAsync_NoSidecar_ReturnsEnvValue()
    {
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBe("env-pass");
    }

    [Fact]
    public async Task ResolveAsync_NoSidecar_EnvNull_ReturnsNull()
    {
        var resolver = Resolver(new StubEnvProvider(null), new FakeBwsRunner(new BwsResult(0, "", "")));

        (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_SidecarEnv_ReturnsEnvValueAndNeverTouchesBws()
    {
        WriteSidecar("""{"source":"env"}""");
        var runner = new FakeBwsRunner(new BwsInvocationException("bws must not run"));
        var resolver = Resolver(new StubEnvProvider("env-pass"), runner);

        (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBe("env-pass");
        runner.Args.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_SidecarBitwarden_FetchesSecretAndDerives()
    {
        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        var resolver = Resolver(new StubEnvProvider("env-pass"), runner);

        var passphrase = (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase;

        passphrase.ShouldBe(DerivedRawKey);
        runner.Args.ShouldBe(["secret", "get", "s-1"]);
        runner.Token.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_SidecarCorrupt_ThrowsLoudNamingThePath()
    {
        WriteSidecar("{not json");
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        var ex = await Should.ThrowAsync<EncryptionSourceException>(() => resolver.ResolveAsync(TestContext.Current.CancellationToken));

        ex.Message.ShouldContain(SidecarPath());
        ex.Message.ShouldContain("corrupt");
    }

    [Fact]
    public async Task ResolveAsync_SidecarBitwardenWithoutSecretId_ThrowsArgumentException()
    {
        WriteSidecar("""{"source":"bitwarden"}""");
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        var ex = await Should.ThrowAsync<ArgumentException>(() => resolver.ResolveAsync(TestContext.Current.CancellationToken));

        ex.ParamName.ShouldBe("encryptionData.SecretId");
    }

    [Fact]
    public async Task ResolveAsync_ReadsSidecarFreshOnEveryCall()
    {
        var resolver = Resolver(new StubEnvProvider("env-pass"),
            new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")));

        (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBe("env-pass");

        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");

        (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBe(DerivedRawKey);
    }

    [Fact]
    public async Task ResolveAsync_NoSidecar_ReportsEnvSourceAndEnvPassphrase()
    {
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        var resolved = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        resolved.SourceName.ShouldBe("env");
        resolved.Passphrase.ShouldBe("env-pass");
    }

    [Fact]
    public async Task ResolveAsync_SidecarBitwarden_ReportsBitwardenSourceAndDerivedKey()
    {
        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var resolver = Resolver(new StubEnvProvider("env-pass"),
            new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), "")));

        var resolved = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        resolved.SourceName.ShouldBe("bitwarden");
        resolved.Passphrase.ShouldBe(DerivedRawKey);
    }

    /// <summary>
    ///     .NET-F2: a Bitwarden install must not shell out to bws on every bank open. The resolved
    ///     key is cached per encryption-source-config fingerprint (sidecar unchanged between calls),
    ///     so N resolves against the same sidecar make exactly one bws invocation.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_CalledRepeatedlyWithUnchangedSidecar_InvokesBwsExactlyOnce()
    {
        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        var resolver = Resolver(new StubEnvProvider("env-pass"), runner);

        for (var i = 0; i < 5; i++)
        {
            (await resolver.ResolveAsync(TestContext.Current.CancellationToken)).Passphrase.ShouldBe(DerivedRawKey);
        }

        runner.CallCount.ShouldBe(1);
    }

    /// <summary>A sidecar change (config verb ran) must invalidate the cache — the fingerprint changed.</summary>
    [Fact]
    public async Task ResolveAsync_SidecarSecretIdChanges_InvokesBwsAgain()
    {
        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        var resolver = Resolver(new StubEnvProvider("env-pass"), runner);

        await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-2"}""");
        await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        runner.CallCount.ShouldBe(2);
    }

    /// <summary>A bank key in a log line is the leak this guards: no call site interpolates the record today, but the compiler-generated ToString would print it.</summary>
    [Fact]
    public void ResolvedKey_ToString_DoesNotContainThePassphraseOrLegacyPassphrase()
    {
        var resolvedKey = new ResolvedKey(DerivedRawKey, "bitwarden")
        {
            LegacyPassphrase = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'"
        };

        var text = resolvedKey.ToString();

        text.ShouldNotContain("72d23870");
        text.ShouldNotContain("277bf737");
        text.ShouldContain("bitwarden");
    }

    [Fact]
    public void Passphrase_ToString_DoesNotContainValueOrLegacyValue()
    {
        var passphrase = new Passphrase("bitwarden")
        {
            Value = DerivedRawKey,
            LegacyValue = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'"
        };

        var text = passphrase.ToString();

        text.ShouldNotContain("72d23870");
        text.ShouldNotContain("277bf737");
        text.ShouldContain("bitwarden");
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string Source => "env";

        public bool IsForSource(string source) => Source.Equals(source, StringComparison.Ordinal);

        public Task<Passphrase> GetPassphraseAsync(EncryptionData encryptionData, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Passphrase(Source) { Value = passphrase });
    }

    private sealed class FakeBwsRunner : ICliSecretManager
    {
        private readonly BwsInvocationException? _exception;
        private readonly BwsResult? _result;

        public FakeBwsRunner(BwsResult result)
        {
            _result = result;
        }

        public FakeBwsRunner(BwsInvocationException exception)
        {
            _exception = exception;
        }

        public IReadOnlyList<string>? Args { get; private set; }
        public string? Token { get; private set; }
        public int CallCount { get; private set; }

        public Task<BwsResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Args = args;
            Token = token;
            CallCount++;
            if (_exception is not null)
            {
                throw _exception;
            }

            return Task.FromResult(_result!);
        }
    }
}
