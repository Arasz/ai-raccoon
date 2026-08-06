using System.Text;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>
///     BitwardenEncryptionKeyProvider: runs `bws secret get` (no -t, 15 s), parses the secret
///     with the Core parser, derives the x'…' raw key (plan §5.1/§5.3/§5.4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BitwardenEncryptionKeyProviderTests
{
    // §5.1 pinned vector: seed 00 01 … 1e 1f → x'277b…'
    private const string DerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";

    [Fact]
    public void GetPassphrase_ValidPem_ReturnsDerivedRawKeyAndRunsSecretGetWithoutToken()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        var provider = new BitwardenEncryptionKeyProvider(runner, "secret-1");

        var passphrase = provider.GetPassphrase();

        passphrase.ShouldBe(DerivedRawKey);
        runner.Args.ShouldBe(["secret", "get", "secret-1"]);
        runner.Token.ShouldBeNull();
        runner.Timeout.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void GetPassphrase_StdoutWithTrailingNewline_StillDerives()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build() + "\n", ""));

        new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase().ShouldBe(DerivedRawKey);
    }

    [Fact]
    public void GetPassphrase_RsaSecret_ThrowsUnsupportedKeyType()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().WithKeyType("ssh-rsa").Build(), ""));

        var ex = Should.Throw<UnsupportedKeyTypeException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldBe("only ed25519 keys are supported");
    }

    [Fact]
    public void GetPassphrase_PassphraseProtectedSecret_ThrowsPassphraseProtected()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().WithEncrypted().Build(), ""));

        var ex = Should.Throw<PassphraseProtectedKeyException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldBe("passphrase-protected keys are not supported");
    }

    [Fact]
    public void GetPassphrase_GarbageStdout_ThrowsMalformed()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, "not a key at all", ""));

        var ex = Should.Throw<MalformedPrivateKeyException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldStartWith("malformed OpenSSH private key: ");
    }

    [Fact]
    public void GetPassphrase_NonZeroExit_ThrowsBwsFailedWithFirstStderrLine()
    {
        var runner = new FakeBwsRunner(new BwsResult(7, "", "network error\nmore detail"));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldBe("bws failed (exit 7): network error");
    }

    [Fact]
    public void GetPassphrase_NonZeroExit_EmptyStderr_StillNamesExitCode()
    {
        var runner = new FakeBwsRunner(new BwsResult(3, "", ""));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldBe("bws failed (exit 3): (no stderr)");
    }

    [Fact]
    public void GetPassphrase_BwsNotFound_PropagatesInstallGuidance()
    {
        var runner = new FakeBwsRunner(new BwsInvocationException(
            "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)"));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldContain("bws not found — install the Bitwarden CLI (bws)");
    }

    [Fact]
    public void GetPassphrase_Timeout_PropagatesTimeoutText()
    {
        var runner = new FakeBwsRunner(new BwsInvocationException("bws timed out after 15s"));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner, "secret-1").GetPassphrase());

        ex.Message.ShouldBe("bws timed out after 15s");
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
        public TimeSpan? Timeout { get; private set; }

        public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout)
        {
            Args = args;
            Token = token;
            Timeout = timeout;
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
        private static readonly byte[] Seed00To1F = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
        private static readonly byte[] PublicKey01To20 = [.. Enumerable.Range(1, 32).Select(i => (byte)i)];
        private uint _checkint1 = 0x01234567;
        private uint _checkint2 = 0x01234567;
        private string _cipherName = "none";
        private bool _invalidBase64;
        private string _kdfName = "none";
        private string _keyType = "ssh-ed25519";

        private string _magic = "openssh-key-v1\0";

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

        public OpenSshKeyBuilder WithInvalidBase64()
        {
            _invalidBase64 = true;
            return this;
        }

        public string Build()
        {
            using var body = new MemoryStream();
            body.Write(Encoding.ASCII.GetBytes(_magic));
            WriteString(body, _cipherName);
            WriteString(body, _kdfName);
            WriteString(body, []);
            WriteUInt32(body, 1);
            WriteString(body, BuildPublicKeyBlob());
            WriteString(body, BuildPrivateSection());

            var base64 = Convert.ToBase64String(body.ToArray());
            if (_invalidBase64)
            {
                base64 = "!!!not-base64!!!";
            }

            return "-----BEGIN OPENSSH PRIVATE KEY-----\n" + base64 + "\n-----END OPENSSH PRIVATE KEY-----\n";
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
            WriteString(section, [.. Seed00To1F, .. PublicKey01To20]);
            WriteString(section, []);
            section.Write(new byte[8 - (int)section.Length % 8]);
            return section.ToArray();
        }

        private static void WriteUInt32(Stream stream, uint value) => stream.Write([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

        private static void WriteString(Stream stream, string value) => WriteString(stream, Encoding.ASCII.GetBytes(value));

        private static void WriteString(Stream stream, byte[] value)
        {
            WriteUInt32(stream, (uint)value.Length);
            stream.Write(value);
        }
    }
}
