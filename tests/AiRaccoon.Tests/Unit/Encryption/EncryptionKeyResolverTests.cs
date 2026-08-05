using System.Text;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     EncryptionKeyResolver: picks the key provider from the memory.db.source sidecar, read
///     fresh on every call — absent sidecar or "env" → env provider; "bitwarden" → bws fetch +
///     derivation; corrupt sidecar → loud error (plan §5.2).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EncryptionKeyResolverTests : IDisposable
{
    // §5.1 pinned vector: seed 00 01 … 1e 1f → x'277b…'
    private const string DerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private EncryptionKeyResolver Resolver(IEncryptionKeyProvider env, FakeBwsRunner runner) => new(Options(), env, runner);

    private void WriteSidecar(string json) => File.WriteAllText(SidecarPath(), json);

    [Fact]
    public void GetPassphrase_NoSidecar_ReturnsEnvValue()
    {
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        resolver.GetPassphrase().ShouldBe("env-pass");
    }

    [Fact]
    public void GetPassphrase_NoSidecar_EnvNull_ReturnsNull()
    {
        var resolver = Resolver(new StubEnvProvider(null), new FakeBwsRunner(new BwsResult(0, "", "")));

        resolver.GetPassphrase().ShouldBeNull();
    }

    [Fact]
    public void GetPassphrase_SidecarEnv_ReturnsEnvValueAndNeverTouchesBws()
    {
        WriteSidecar("""{"source":"env"}""");
        var runner = new FakeBwsRunner(new BwsInvocationException("bws must not run"));
        var resolver = Resolver(new StubEnvProvider("env-pass"), runner);

        resolver.GetPassphrase().ShouldBe("env-pass");
        runner.Args.ShouldBeNull();
    }

    [Fact]
    public void GetPassphrase_SidecarBitwarden_FetchesSecretAndDerives()
    {
        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        var resolver = Resolver(new StubEnvProvider("env-pass"), runner);

        var passphrase = resolver.GetPassphrase();

        passphrase.ShouldBe(DerivedRawKey);
        runner.Args.ShouldBe(["secret", "get", "s-1"]);
        runner.Token.ShouldBeNull();
    }

    [Fact]
    public void GetPassphrase_SidecarCorrupt_ThrowsLoudNamingThePath()
    {
        WriteSidecar("{not json");
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        var ex = Should.Throw<EncryptionSourceException>(() => resolver.GetPassphrase());

        ex.Message.ShouldContain(SidecarPath());
        ex.Message.ShouldContain("corrupt");
    }

    [Fact]
    public void GetPassphrase_SidecarBitwardenWithoutSecretId_ThrowsLoud()
    {
        WriteSidecar("""{"source":"bitwarden"}""");
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        var ex = Should.Throw<EncryptionSourceException>(() => resolver.GetPassphrase());

        ex.Message.ShouldContain(SidecarPath());
        ex.Message.ShouldContain("secretId");
    }

    [Fact]
    public void GetPassphrase_ReadsSidecarFreshOnEveryCall()
    {
        var resolver = Resolver(new StubEnvProvider("env-pass"),
            new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), "")));

        resolver.GetPassphrase().ShouldBe("env-pass");

        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");

        resolver.GetPassphrase().ShouldBe(DerivedRawKey);
    }

    [Fact]
    public void Resolve_NoSidecar_ReportsEnvSourceAndEnvPassphrase()
    {
        var resolver = Resolver(new StubEnvProvider("env-pass"), new FakeBwsRunner(new BwsResult(0, "", "")));

        var resolved = resolver.Resolve();

        resolved.SourceName.ShouldBe(EncryptionSettingsKeys.SourceEnv);
        resolved.Passphrase.ShouldBe("env-pass");
    }

    [Fact]
    public void Resolve_SidecarBitwarden_ReportsBitwardenSourceAndDerivedKey()
    {
        WriteSidecar("""{"source":"bitwarden","projectId":"p-1","secretId":"s-1"}""");
        var resolver = Resolver(new StubEnvProvider("env-pass"),
            new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), "")));

        var resolved = resolver.Resolve();

        resolved.SourceName.ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        resolved.Passphrase.ShouldBe(DerivedRawKey);
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string? GetPassphrase() => passphrase;
    }

    private sealed class FakeBwsRunner : IBwsProcessRunner
    {
        private readonly BwsResult? _result;
        private readonly BwsInvocationException? _exception;

        public FakeBwsRunner(BwsResult result) => _result = result;
        public FakeBwsRunner(BwsInvocationException exception) => _exception = exception;

        public IReadOnlyList<string>? Args { get; private set; }
        public string? Token { get; private set; }

        public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout)
        {
            Args = args;
            Token = token;
            if (_exception is not null)
            {
                throw _exception;
            }

            return _result!;
        }
    }

    /// <summary>Assembles an openssh-key-v1 blob from synthetic bytes — deterministic, no real key material.</summary>
    private sealed class OpenSshKeyBuilder
    {
        private static readonly byte[] Seed00To1F = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        private static readonly byte[] PublicKey01To20 = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        private string _magic = "openssh-key-v1\0";
        private string _cipherName = "none";
        private string _kdfName = "none";
        private string _keyType = "ssh-ed25519";
        private uint _checkint1 = 0x01234567;
        private uint _checkint2 = 0x01234567;

        public OpenSshKeyBuilder WithEncrypted(string cipherName = "aes256-ctr", string kdfName = "bcrypt")
        {
            _cipherName = cipherName;
            _kdfName = kdfName;
            return this;
        }

        public OpenSshKeyBuilder WithKeyType(string keyType)
        {
            _keyType = keyType;
            return this;
        }

        public string Build()
        {
            using var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes(_magic));
            WriteString(body, _cipherName);
            WriteString(body, _kdfName);
            WriteString(body, Array.Empty<byte>());
            WriteUInt32(body, 1);
            WriteString(body, BuildPublicKeyBlob());
            WriteString(body, BuildPrivateSection());

            return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + Convert.ToBase64String(body.ToArray())
                + "\n-----END OPENSSH PRIVATE KEY-----\n";
        }

        private byte[] BuildPublicKeyBlob()
        {
            using var blob = new MemoryStream();
            WriteString(blob, _keyType);
            WriteString(blob, PublicKey01To20);
            return blob.ToArray();
        }

        private byte[] BuildPrivateSection()
        {
            using var section = new MemoryStream();
            WriteUInt32(section, _checkint1);
            WriteUInt32(section, _checkint2);
            WriteString(section, _keyType);
            WriteString(section, PublicKey01To20);
            WriteString(section, Seed00To1F.Concat(PublicKey01To20).ToArray());
            WriteString(section, Array.Empty<byte>());
            section.Write(new byte[8 - ((int)section.Length % 8)]);
            return section.ToArray();
        }

        private static void WriteUInt32(Stream stream, uint value) =>
            stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

        private static void WriteString(Stream stream, byte[] value)
        {
            WriteUInt32(stream, (uint)value.Length);
            stream.Write(value);
        }
    }
}
