using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

/// <summary>OnePasswordEncryptionKeyProvider: `op read &lt;reference&gt;` (null token, 30 s), parse, derive; no legacy key (ADR-0132).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class OnePasswordEncryptionKeyProviderTests
{
    // Pinned vector: seed 00 01 … 1e 1f
    private const string DerivedRawKey = "x'72d23870a80905c7043e610ec6609b352a85b07f14dbe4358e9b5ffcb50a3485'";
    private const string Reference = "op://vault/item/private key?ssh-format=openssh";

    private static EncryptionData Data(string? reference = Reference) => new("1password") { SecretId = reference };

    [Fact]
    public void Source_IsOnePassword()
    {
        var provider = new OnePasswordEncryptionKeyProvider(new FakeOpRunner(new CliSecretResult(0, "", "")));

        provider.Source.ShouldBe("1password");
        provider.IsForSource("1password").ShouldBeTrue();
        provider.IsForSource("bitwarden").ShouldBeFalse();
    }

    [Fact]
    public async Task GetPassphraseAsync_ValidKey_ReturnsGoldenKeyAndRunsOpReadWithoutTokenWithin30Seconds()
    {
        var runner = new FakeOpRunner(new CliSecretResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var passphrase = await new OnePasswordEncryptionKeyProvider(runner).GetPassphraseAsync(Data(), TestContext.Current.CancellationToken);

        passphrase.Value.ShouldBe(DerivedRawKey);
        passphrase.Source.ShouldBe("1password");
        runner.Args.ShouldBe(["read", Reference]);
        runner.Token.ShouldBeNull();
        runner.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GetPassphraseAsync_ValidKey_HasNoLegacyValue()
    {
        var runner = new FakeOpRunner(new CliSecretResult(0, new TestOpenSshKeyBuilder().Build(), ""));

        var passphrase = await new OnePasswordEncryptionKeyProvider(runner).GetPassphraseAsync(Data(), TestContext.Current.CancellationToken);

        passphrase.LegacyValue.ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task GetPassphraseAsync_MissingReference_ThrowsNamingTheConfigCommand(string? reference)
    {
        var runner = new FakeOpRunner(new CliSecretResult(0, "", ""));

        var ex = await Should.ThrowAsync<EncryptionSourceException>(() => new OnePasswordEncryptionKeyProvider(runner).GetPassphraseAsync(Data(reference), TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("'ai-raccoon encryption 1password'");
        runner.Args.ShouldBeNull();
    }

    [Fact]
    public async Task GetPassphraseAsync_NonZeroExit_ThrowsFailedWithFirstStderrLine()
    {
        var runner = new FakeOpRunner(new CliSecretResult(1, "", "[ERROR] item not found\nmore detail"));

        var ex = await Should.ThrowAsync<CliSecretInvocationException>(() => new OnePasswordEncryptionKeyProvider(runner).GetPassphraseAsync(Data(), TestContext.Current.CancellationToken));

        ex.Failure.ShouldBe(CliSecretFailure.Failed);
        ex.Message.ShouldBe("op failed (exit 1): [ERROR] item not found");
    }

    [Fact]
    public async Task GetPassphraseAsync_GarbageStdout_ThrowsMalformed()
    {
        var runner = new FakeOpRunner(new CliSecretResult(0, "not a key at all", ""));

        await Should.ThrowAsync<MalformedPrivateKeyException>(() => new OnePasswordEncryptionKeyProvider(runner).GetPassphraseAsync(Data(), TestContext.Current.CancellationToken));
    }

    private sealed class FakeOpRunner(CliSecretResult result) : ICliSecretManager
    {
        public IReadOnlyList<string>? Args { get; private set; }
        public string? Token { get; private set; }
        public TimeSpan? Timeout { get; private set; }

        public Task<CliSecretResult> RunAsync(IReadOnlyList<string> args, string? token, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Args = args;
            Token = token;
            Timeout = timeout;
            return Task.FromResult(result);
        }
    }
}
