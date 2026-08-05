using System.Text;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     encryption bitwarden/show/unset: bws presence with install guidance, interactive id
///     collection (owner defaults), per-run-only token, reachability validation, rotation
///     warning, rekey→sidecar→settings persist order, the amendment-1 env-key retry leg,
///     and the unset rekey-back/recovery paths (plan §S3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ConfigCommandsEncryptionTests : IDisposable
{
    // §5.1 pinned vector: seed 00 01 … 1e 1f → x'277b…' (the OpenSshKeyBuilder below builds that seed).
    private const string DerivedRawKey = "x'277bf737b8e8f3f7de45d6b930028f22b1a9a417e63fb3db8ed8d773744d281b'";
    private const string DefaultProjectId = "613165e6-7947-49e0-889b-b49d007c5b85";
    private const string DefaultSecretId = "f1d3c8e5-5391-4aef-8611-b49d007c8702";

    private const string BwsNotFoundText =
        "bws not found — install the Bitwarden CLI (bws) and configure BWS_ACCESS_TOKEN (https://bitwarden.com/help/cli/)";

    // Tests that set/clear AIRACCOON_DB_PASSPHRASE are serialized with ConfigVerbRunnerTests
    // via TestData.EnvVarGate (the env var is process-global).
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private InfrastructureOptions Options() => new() { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };

    private string BankPath() => SqliteConnectionFactory.BankPathFor(Options());

    private string SidecarPath() => EncryptionSourceSidecar.PathFor(BankPath());

    private void WriteSidecar(string source, string projectId, string secretId) => new EncryptionSourceSidecar(BankPath()).Write(new EncryptionSourceConfig(source, projectId, secretId));

    private async Task<(int Exit, string Out, string Err, SqliteConnectionFactory Bank)> Run(string[] args,
        FakeConfigStore store, FakeBwsRunner runner, TextReader? stdin = null, string? envPassphrase = null)
    {
        var parsed = CliArgs.Parse(args);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider(envPassphrase), runner));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr,
            stdin ?? TextReader.Null, TestContext.Current.CancellationToken, bank, runner,
            new StubEnvProvider(envPassphrase));
        return (exit, stdout.ToString(), stderr.ToString(), bank);
    }

    private static T WithEnvPassphrase<T>(string? value, Func<T> action)
    {
        TestData.EnvVarGate.Wait();
        try
        {
            var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
            try
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, value);
                return action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            }
        }
        finally
        {
            TestData.EnvVarGate.Release();
        }
    }

    // ── encryption bitwarden: presence, collection, token, validation ──

    [Fact]
    public async Task Bitwarden_BwsMissing_ReturnsInstallErrorAndChangesNothing()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsInvocationException(BwsNotFoundText));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(1);
        err.ShouldContain("bws not found");
        err.ShouldContain("https://bitwarden.com/help/cli/");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task Bitwarden_InteractiveOwnerDefaults_PersistsSourceAndWarns()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(0);
        stdout.ShouldContain("encryption source set to bitwarden");
        err.ShouldContain("without PRAGMA rekey bricks the bank");
        err.ShouldContain($"project id [{DefaultProjectId}]");
        err.ShouldContain($"secret id [{DefaultSecretId}]");
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        store.Settings[EncryptionSettingsKeys.ProjectId].ShouldBe(DefaultProjectId);
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe(DefaultSecretId);
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.Source.ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        sidecar.ProjectId.ShouldBe(DefaultProjectId);
        sidecar.SecretId.ShouldBe(DefaultSecretId);
        runner.Calls.Count.ShouldBe(2);
        runner.Calls[0].Args.ShouldBe(["--version"]);
        runner.Calls[0].Token.ShouldBeNull();
        runner.Calls[1].Args.ShouldBe(["secret", "get", DefaultSecretId]);
        runner.Calls[1].Token.ShouldBeNull();
    }

    [Fact]
    public async Task Bitwarden_NonDefaultIdsViaStdin_ArePersisted()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));

        var (exit, _, _, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("p-111\ns-222\n"));

        exit.ShouldBe(0);
        store.Settings[EncryptionSettingsKeys.ProjectId].ShouldBe("p-111");
        store.Settings[EncryptionSettingsKeys.SecretId].ShouldBe("s-222");
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.ProjectId.ShouldBe("p-111");
        sidecar.SecretId.ShouldBe("s-222");
    }

    [Fact]
    public async Task Bitwarden_Token_UsedForValidationOnlyNeverPersisted()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));

        var (exit, _, _, _) = await Run(["encryption", "bitwarden", "-t", "tok-123"], store, runner,
            new StringReader("\n\n"));

        exit.ShouldBe(0);
        runner.Calls[0].Token.ShouldBeNull(); // the presence check never takes the token
        runner.Calls[1].Token.ShouldBe("tok-123");
        store.Settings.Values.ShouldNotContain(v => v.Contains("tok-123"));
        File.ReadAllText(SidecarPath()).ShouldNotContain("tok-123");
    }

    [Fact]
    public async Task Bitwarden_UnreachableSecret_ReturnsBwsErrorAndNoChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(1, "", "secret not found (code: 404)"));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(1);
        err.ShouldContain("bws failed (exit 1)");
        err.ShouldContain("secret not found (code: 404)");
        err.ShouldNotContain("PRAGMA rekey");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    [Fact]
    public async Task Bitwarden_MalformedSecretValue_ReturnsMalformedErrorAndNoChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, "not an openssh key", ""));

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"));

        exit.ShouldBe(1);
        err.ShouldContain("malformed OpenSSH private key");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
    }

    // ── encryption bitwarden: bank rekey / self-heal / env-key retry leg ──

    [Fact]
    public async Task Bitwarden_EnvKeyedBank_RekeysToDerivedKey()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider("env-pass"), runner));
        await using (var seed = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, _, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"),
            "env-pass");

        exit.ShouldBe(0);
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        var sidecar = new EncryptionSourceSidecar(BankPath()).Read();
        sidecar.ShouldNotBeNull();
        sidecar.Source.ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        // The bank now opens with the derived key (via the resolver: sidecar → bws fetch).
        await using (var reopened = await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var wrongKey = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken);
        });
        wrongKey.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task Bitwarden_SelfHeal_BankAlreadyDerivedKeyed_SkipsRekeyAndPersists()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider("env-pass"), runner));
        await using (var seed = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, _, _) = await Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n"),
            "env-pass");

        exit.ShouldBe(0);
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        File.Exists(SidecarPath()).ShouldBeTrue();
        await using (var reopened = await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task Bitwarden_StaleSidecarAndEnvKeyedBank_ReportsEnvKeyedAndDeletesSidecar()
    {
        // Amendment 1 (unset crash window): bank rekeyed back to env, sidecar never deleted.
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider(null), runner));
        await using (var seed = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await Run(["encryption", "bitwarden"], store, runner,
            new StringReader("\n\n"), "env-pass");

        exit.ShouldBe(0);
        err.ShouldContain("bank is env-keyed");
        err.ShouldContain("source was not switched");
        File.Exists(SidecarPath()).ShouldBeFalse();
        store.Settings.ShouldBeEmpty();
        await using (var reopened = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
    public async Task Bitwarden_StaleSidecarAndEnvKeyedBank_NoEnvPassphrase_ErrorsWithoutChange()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider(null), runner));
        await using (var seed = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var (exit, _, err, _) = await WithEnvPassphrase(null, () =>
            Run(["encryption", "bitwarden"], store, runner, new StringReader("\n\n")));

        exit.ShouldBe(1);
        err.ShouldContain("encryption mismatch");
        File.Exists(SidecarPath()).ShouldBeTrue();
        store.Settings.ShouldBeEmpty();
        await using (var reopened = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    // ── encryption show ──

    [Fact]
    public async Task Show_NoRowsNoSidecar_PrintsEnvSource()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("source: env");
    }

    [Fact]
    public async Task Show_BitwardenRows_PrintsSourceAndIds()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = EncryptionSettingsKeys.SourceBitwarden,
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.ShouldContain("source: bitwarden");
        stdout.ShouldContain($"projectId: {DefaultProjectId}");
        stdout.ShouldContain($"secretId: {DefaultSecretId}");
    }

    [Fact]
    public async Task Show_NoRows_SidecarFallback_PrintsBitwardenWithIds()
    {
        var store = new FakeConfigStore();
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, "p-9", "s-9");

        var (exit, stdout, _, _) = await Run(["encryption", "show"], store, runner);

        exit.ShouldBe(0);
        stdout.ShouldContain("source: bitwarden");
        stdout.ShouldContain("projectId: p-9");
        stdout.ShouldContain("secretId: s-9");
    }

    // ── encryption unset ──

    [Fact]
    public async Task Unset_NoBank_RemovesRowsAndSidecar()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = EncryptionSettingsKeys.SourceBitwarden,
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, DefaultProjectId, DefaultSecretId);

        var (exit, stdout, err, _) = await Run(["encryption", "unset"], store, runner);

        // No bank exists — nothing is stranded, so the cleanup runs regardless of the env passphrase.
        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("encryption source reset to env");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        err.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unset_RealBank_RekeysBackToEnvPassphrase()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = EncryptionSettingsKeys.SourceBitwarden,
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider(null), runner));
        await using (var seed = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, stdout, _, _) = await Run(["encryption", "unset"], store, runner, envPassphrase: "env-pass");

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("encryption source reset to env");
        store.Settings.ShouldBeEmpty();
        File.Exists(SidecarPath()).ShouldBeFalse();
        await using (var reopened = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }

        var wrongKey = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken);
        });
        wrongKey.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task Unset_RealBank_NoEnvPassphrase_WarnsAndKeepsBankOnDerivedKey()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = EncryptionSettingsKeys.SourceBitwarden,
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider(null), runner));
        await using (var seed = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var (exit, stdout, err, _) = await Run(["encryption", "unset"], store, runner);

        exit.ShouldBe(1);
        err.ShouldContain("stays keyed to the bitwarden secret");
        err.ShouldContain("set AIRACCOON_DB_PASSPHRASE and re-run");
        // The sidecar + rows stay (source remains bitwarden) so the documented retry works.
        store.Settings[EncryptionSettingsKeys.Source].ShouldBe(EncryptionSettingsKeys.SourceBitwarden);
        File.Exists(SidecarPath()).ShouldBeTrue();
        await using (var reopened = await bank.OpenBankWithKeyAsync(DerivedRawKey, TestContext.Current.CancellationToken))
        {
        }

        var wrongKey = await Should.ThrowAsync<SqliteException>(async () =>
        {
            await using var _ = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken);
        });
        wrongKey.SqliteErrorCode.ShouldBe(26);
    }

    [Fact]
    public async Task Unset_RealBank_RekeysBackFromResolverCreatedBank()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                [EncryptionSettingsKeys.Source] = EncryptionSettingsKeys.SourceBitwarden,
                [EncryptionSettingsKeys.ProjectId] = DefaultProjectId,
                [EncryptionSettingsKeys.SecretId] = DefaultSecretId
            }
        };
        var runner = new FakeBwsRunner(new BwsResult(0, new OpenSshKeyBuilder().Build(), ""));
        WriteSidecar(EncryptionSettingsKeys.SourceBitwarden, DefaultProjectId, DefaultSecretId);
        var bank = new SqliteConnectionFactory(Options(),
            new EncryptionKeyResolver(Options(), new StubEnvProvider("env-pass"), runner));
        await using (var seed = await bank.OpenBankAsync(TestContext.Current.CancellationToken))
        {
        }

        var (exit, stdout, err, _) = await Run(["encryption", "unset"], store, runner, envPassphrase: "env-pass");

        exit.ShouldBe(0, $"stderr: {err}");
        await using (var reopened = await bank.OpenBankWithKeyAsync("env-pass", TestContext.Current.CancellationToken))
        {
        }
    }

    private sealed class StubEnvProvider(string? passphrase) : IEncryptionKeyProvider
    {
        public string? GetPassphrase() => passphrase;
    }

    private sealed class FakeBwsRunner : IBwsProcessRunner
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

        public List<(IReadOnlyList<string> Args, string? Token)> Calls { get; } = [];

        public BwsResult Run(IReadOnlyList<string> args, string? token, TimeSpan timeout)
        {
            Calls.Add(([.. args], token));
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
