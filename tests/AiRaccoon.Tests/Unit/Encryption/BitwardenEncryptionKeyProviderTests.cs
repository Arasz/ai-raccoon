using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
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
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().Build(), ""));
        var provider = new BitwardenEncryptionKeyProvider(runner);

        var passphrase = provider.GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" });

        passphrase.Value.ShouldBe(DerivedRawKey);
        runner.Args.ShouldBe(["secret", "get", "secret-1"]);
        runner.Token.ShouldBeNull();
        runner.Timeout.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void GetPassphrase_StdoutWithTrailingNewline_StillDerives()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, $"{new TestOpenSshKeyBuilder().Build()}\n", ""));

        new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }).Value.ShouldBe(DerivedRawKey);
    }

    [Fact]
    public void GetPassphrase_RsaSecret_ThrowsUnsupportedKeyType()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().WithKeyType("ssh-rsa").Build(), ""));

        var ex = Should.Throw<UnsupportedKeyTypeException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

        ex.Message.ShouldBe("only ed25519 keys are supported");
    }

    [Fact]
    public void GetPassphrase_PassphraseProtectedSecret_ThrowsPassphraseProtected()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, new TestOpenSshKeyBuilder().WithEncrypted().Build(), ""));

        var ex = Should.Throw<PassphraseProtectedKeyException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

        ex.Message.ShouldBe("passphrase-protected keys are not supported");
    }

    [Fact]
    public void GetPassphrase_GarbageStdout_ThrowsMalformed()
    {
        var runner = new FakeBwsRunner(new BwsResult(0, "not a key at all", ""));

        var ex = Should.Throw<MalformedPrivateKeyException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

        ex.Message.ShouldStartWith("malformed OpenSSH private key: ");
    }

    [Fact]
    public void GetPassphrase_NonZeroExit_ThrowsBwsFailedWithFirstStderrLine()
    {
        var runner = new FakeBwsRunner(new BwsResult(7, "", "network error\nmore detail"));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

        ex.Message.ShouldBe("bws failed (exit 7): network error");
    }

    [Fact]
    public void GetPassphrase_NonZeroExit_EmptyStderr_StillNamesExitCode()
    {
        var runner = new FakeBwsRunner(new BwsResult(3, "", ""));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

        ex.Message.ShouldBe("bws failed (exit 3): (no stderr)");
    }

    [Fact]
    public void GetPassphrase_BwsNotFound_PropagatesInstallGuidance()
    {
        var runner = new FakeBwsRunner(new BwsInvocationException(
            "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)"));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

        ex.Message.ShouldContain("bws not found — install the Bitwarden CLI (bws)");
    }

    [Fact]
    public void GetPassphrase_Timeout_PropagatesTimeoutText()
    {
        var runner = new FakeBwsRunner(new BwsInvocationException("bws timed out after 15s"));

        var ex = Should.Throw<BwsInvocationException>(() => new BitwardenEncryptionKeyProvider(runner).GetPassphrase(new EncryptionData("bitwarden") { SecretId = "secret-1" }));

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
}
