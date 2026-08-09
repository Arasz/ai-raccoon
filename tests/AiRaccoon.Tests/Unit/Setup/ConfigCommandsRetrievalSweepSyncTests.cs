using AiRaccoon.Core.Degradation;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     retrieval alpha, sweep policy (kill switch, cadence, threshold) and sync config
///     commands: settings-table writes with validation and redacted key output.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsRetrievalSweepSyncTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store,
        TextReader? stdin = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await new ConfigCommands(settings: new SettingsCommands(), sync: new SyncCommands()).RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr, stdin ?? TextReader.Null, cancellationToken: TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    // ── retrieval alpha ──

    [Fact]
    public async Task RetrievalAlphaSet_WritesSetting()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["retrieval", "alpha", "set", "0.3"], store);

        exit.ShouldBe(0);
        store.Settings["retrieval.structureAlpha"].ShouldBe("0.3");
    }

    [Fact]
    public async Task RetrievalAlphaSet_OutOfRange_ReturnsError()
    {
        foreach (var value in new[] { "-0.1", "1.5" })
        {
            var store = new FakeConfigStore();

            var (exit, _, err) = await Run(["retrieval", "alpha", "set", value], store);

            exit.ShouldBe(1);
            err.ShouldContain("0..1");
            store.Settings.ShouldNotContainKey("retrieval.structureAlpha");
        }
    }

    [Fact]
    public async Task RetrievalAlphaSet_InvalidNumber_ReturnsError()
    {
        var (exit, _, err) = await Run(["retrieval", "alpha", "set", "abc"], new FakeConfigStore());

        exit.ShouldBe(1);
        err.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RetrievalAlphaShow_NoRow_PrintsDefault()
    {
        var (exit, stdout, _) = await Run(["retrieval", "alpha", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("0.5");
    }

    [Fact]
    public async Task RetrievalAlphaShow_WithRow_PrintsRowValue()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["retrieval.structureAlpha"] = "0.8"
            }
        };

        var (_, stdout, _) = await Run(["retrieval", "alpha", "show"], store);

        stdout.Trim().ShouldBe("0.8");
    }

    // ── sweep kill switch ──

    [Fact]
    public async Task SweepDisable_WritesFalse_AndShowReportsDisabled()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["sweep", "disable"], store);

        exit.ShouldBe(0);
        store.Settings["sweep.enabled.global"].ShouldBe("false");
        stdout.ShouldContain("disabled");

        var (_, shown, _) = await Run(["sweep", "show"], store);
        shown.ShouldContain("enabled: False");
    }

    [Fact]
    public async Task SweepEnable_RestoresTheReaper_AndShowReportsEnabled()
    {
        var store = new FakeConfigStore { Settings = { ["sweep.enabled.global"] = "false" } };

        var (exit, stdout, _) = await Run(["sweep", "enable"], store);

        exit.ShouldBe(0);
        store.Settings["sweep.enabled.global"].ShouldBe("true");
        stdout.ShouldContain("enabled");

        var (_, shown, _) = await Run(["sweep", "show"], store);
        shown.ShouldContain("enabled: True");
    }

    /// <summary>What `sweep disable` writes must disarm the reaper through the parse the service uses.</summary>
    [Fact]
    public async Task SweepDisable_WritesAValueThatParseEnabledReadsAsOff()
    {
        var store = new FakeConfigStore();

        await Run(["sweep", "disable"], store);

        SweepConfigKeys.ParseEnabled(store.Settings["sweep.enabled.global"]).ShouldBeFalse();
    }

    // A default-ON deleter must fail safe: any casing of "false" disarms it, and `show`
    // must agree with the service rather than reporting the raw row.
    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public async Task SweepShow_AnyCasingOfFalse_ReportsDisabled(string raw)
    {
        var store = new FakeConfigStore { Settings = { ["sweep.enabled.global"] = raw } };

        var (exit, stdout, _) = await Run(["sweep", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("enabled: False");
    }

    // ── sweep interval ──

    [Fact]
    public async Task SweepIntervalHours_WritesSetting()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await Run(["sweep", "interval-hours", "6"], store);

        exit.ShouldBe(0);
        store.Settings["sweep.interval-hours.global"].ShouldBe("6");
        stdout.ShouldContain("6");
    }

    [Fact]
    public async Task SweepIntervalHours_OutOfRange_ReturnsErrorWithoutWriting()
    {
        foreach (var value in new[] { "0", "8761" })
        {
            var store = new FakeConfigStore();

            var (exit, _, err) = await Run(["sweep", "interval-hours", value], store);

            exit.ShouldBe(1);
            err.ShouldContain("1..8760");
            store.Settings.ShouldNotContainKey("sweep.interval-hours.global");
        }
    }

    [Fact]
    public async Task SweepIntervalHours_InvalidNumber_ReturnsError()
    {
        var (exit, _, err) = await Run(["sweep", "interval-hours", "soon"], new FakeConfigStore());

        exit.ShouldBe(1);
        err.ShouldContain("invalid interval");
    }

    // ── sweep threshold ──

    [Fact]
    public async Task SweepThresholdSet_WritesSetting()
    {
        var store = new FakeConfigStore();

        var (exit, _, _) = await Run(["sweep", "threshold", "set", "0.2"], store);

        exit.ShouldBe(0);
        store.Settings["sweep.threshold"].ShouldBe("0.2");
    }

    [Fact]
    public async Task SweepThresholdSet_OutOfRange_ReturnsError()
    {
        var (exit, _, err) = await Run(["sweep", "threshold", "set", "1.5"], new FakeConfigStore());

        exit.ShouldBe(1);
        err.ShouldContain("0..1");
    }

    [Fact]
    public async Task SweepThresholdSet_InvalidNumber_ReturnsError()
    {
        var (exit, _, err) = await Run(["sweep", "threshold", "set", "bogus"], new FakeConfigStore());

        exit.ShouldBe(1);
        err.ShouldContain("invalid threshold");
    }

    // ── sweep show (the whole policy: a reader must be able to tell whether anything is armed) ──

    [Fact]
    public async Task SweepShow_NoRows_PrintsTheDefaultPolicy()
    {
        var (exit, stdout, _) = await Run(["sweep", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("enabled: True  interval: 24 h  threshold: 0.3");
    }

    [Fact]
    public async Task SweepShow_WithRows_PrintsTheStoredPolicy()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sweep.enabled.global"] = "false",
                ["sweep.interval-hours.global"] = "72",
                ["sweep.threshold"] = "0.1"
            }
        };

        var (_, stdout, _) = await Run(["sweep", "show"], store);

        stdout.Trim().ShouldBe("enabled: False  interval: 72 h  threshold: 0.1");
    }

    // ── sync add / remove / show ──

    [Fact]
    public async Task SyncAddS3_WritesAllRows_IncludingSecrets_PromptedInteractively()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, stderr) = await Run(
            [
                "sync", "add", "s3", "http://s3.example.com",
                "--bucket", "memories", "--region", "us-east-1", "--object-key", "bank.db"
            ], store,
            new StringReader("ak1\nsk1\n"));

        exit.ShouldBe(0);
        store.Settings["sync.provider"].ShouldBe("s3");
        store.Settings["sync.endpoint"].ShouldBe("http://s3.example.com");
        store.Settings["sync.bucket"].ShouldBe("memories");
        store.Settings["sync.region"].ShouldBe("us-east-1");
        store.Settings["sync.objectKey"].ShouldBe("bank.db");
        store.Settings["sync.accessKey"].ShouldBe("ak1");
        store.Settings["sync.secretKey"].ShouldBe("sk1");
        stdout.ShouldContain("s3");
        stderr.ShouldContain("access key");
    }

    [Fact]
    public async Task SyncAddS3_WritesProviderRow_AndClearsAzureRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.connectionString"] = "connstr",
                ["sync.container"] = "memories",
                ["sync.objectKey"] = "bank.db"
            }
        };

        var (exit, _, _) = await Run(
            ["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories", "--object-key", "bank.db"],
            store, new StringReader("ak1\nsk1\n"));

        exit.ShouldBe(0);
        store.Settings["sync.provider"].ShouldBe("s3");
        store.Settings["sync.endpoint"].ShouldBe("http://s3.example.com");
        store.Settings["sync.bucket"].ShouldBe("memories");
        store.Settings["sync.objectKey"].ShouldBe("bank.db");
        store.Settings.ShouldNotContainKey("sync.connectionString");
        store.Settings.ShouldNotContainKey("sync.container");
    }

    [Fact]
    public async Task SyncAddS3_EmptyStdin_FailsWithoutPersistingAnything()
    {
        var store = new FakeConfigStore();

        var (exit, _, stderr) = await Run(
            ["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories"], store,
            new StringReader(""));

        exit.ShouldBe(1);
        stderr.ShouldContain("key required");
        store.Settings.ShouldNotContainKey("sync.endpoint");
        store.Settings.ShouldNotContainKey("sync.accessKey");
        store.Settings.ShouldNotContainKey("sync.secretKey");
    }

    [Fact]
    public async Task SyncAddS3_WithoutRegionOrObjectKey_ClearsStaleRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.region"] = "eu-west-1",
                ["sync.objectKey"] = "old.db"
            }
        };

        await Run(["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories"], store,
            new StringReader("ak1\nsk1\n"));

        store.Settings["sync.endpoint"].ShouldBe("http://s3.example.com");
        store.Settings.ShouldNotContainKey("sync.region");
        store.Settings.ShouldNotContainKey("sync.objectKey");
    }

    [Fact]
    public async Task SyncAddAzure_WritesRows_AndClearsS3Rows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.region"] = "us-east-1",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1",
                ["sync.objectKey"] = "old.db"
            }
        };

        var (exit, stdout, _) = await Run(
            ["sync", "add", "azure", "memories", "--object-key", "bank.db"], store,
            new StringReader("connstr\n"));

        exit.ShouldBe(0);
        store.Settings["sync.provider"].ShouldBe("azure");
        store.Settings["sync.connectionString"].ShouldBe("connstr");
        store.Settings["sync.container"].ShouldBe("memories");
        // objectKey is shared across providers: upserted, never blanket-deleted.
        store.Settings["sync.objectKey"].ShouldBe("bank.db");
        store.Settings.ShouldNotContainKey("sync.endpoint");
        store.Settings.ShouldNotContainKey("sync.bucket");
        store.Settings.ShouldNotContainKey("sync.region");
        store.Settings.ShouldNotContainKey("sync.accessKey");
        store.Settings.ShouldNotContainKey("sync.secretKey");
        stdout.ShouldContain("azure container memories");
    }

    [Fact]
    public async Task SyncAddAzure_EmptyStdin_AbortsWithoutPersistingAnything()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1"
            }
        };

        var (exit, _, stderr) = await Run(["sync", "add", "azure", "memories"], store, new StringReader(""));

        exit.ShouldBe(1);
        stderr.ShouldContain("connection string required");
        // Nothing written or deleted: the s3 install stays untouched (no provider flip).
        store.Settings.ShouldNotContainKey("sync.provider");
        store.Settings.ShouldNotContainKey("sync.connectionString");
        store.Settings.ShouldNotContainKey("sync.container");
        store.Settings["sync.endpoint"].ShouldBe("http://s3.example.com");
        store.Settings["sync.bucket"].ShouldBe("memories");
    }

    [Fact]
    public async Task SyncAddAzure_WithoutObjectKey_ClearsStaleObjectKeyRow()
    {
        var store = new FakeConfigStore
        {
            Settings = { ["sync.objectKey"] = "old.db" }
        };

        await Run(["sync", "add", "azure", "memories"], store, new StringReader("connstr\n"));

        store.Settings["sync.container"].ShouldBe("memories");
        store.Settings.ShouldNotContainKey("sync.objectKey");
    }

    [Fact]
    public async Task SyncRemove_DeletesAllSyncRows_IncludingAzure()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1",
                ["sync.connectionString"] = "connstr",
                ["sync.container"] = "memories",
                ["sync.future"] = "x"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "remove"], store);

        exit.ShouldBe(0);
        store.Settings.Keys.ShouldNotContain(k => k.StartsWith("sync.", StringComparison.Ordinal));
        stdout.ShouldContain("off");
    }

    [Fact]
    public async Task SyncShow_NotConfigured_PrintsMessage()
    {
        var (exit, stdout, _) = await Run(["sync", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.ShouldContain("not configured");
    }

    [Fact]
    public async Task SyncShow_Azure_PrintsProviderContainerAndRedactedConnectionString()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.connectionString"] = "connstr",
                ["sync.container"] = "memories",
                ["sync.objectKey"] = "bank.db"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: azure");
        stdout.ShouldContain("container: memories");
        stdout.ShouldContain("objectKey: bank.db");
        stdout.ShouldContain("connectionString: set");
        stdout.ShouldNotContain("connstr");
    }

    [Fact]
    public async Task SyncShow_WithoutProviderRow_DefaultsToS3()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: s3");
        stdout.ShouldContain("endpoint: http://s3.example.com");
        stdout.ShouldContain("bucket: memories");
    }

    [Fact]
    public async Task SyncShow_AzureMissingSecret_ShowsUnset()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.container"] = "memories"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: azure");
        stdout.ShouldContain("container: memories");
        stdout.ShouldContain("connectionString: unset");
    }

    [Fact]
    public async Task SyncShow_Configured_PrintsRedactedSecrets()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.region"] = "us-east-1",
                ["sync.objectKey"] = "bank.db",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: s3");
        stdout.ShouldContain("http://s3.example.com");
        stdout.ShouldContain("memories");
        stdout.ShouldContain("region: us-east-1");
        stdout.ShouldContain("Key: set");
        stdout.ShouldNotContain("ak1");
        stdout.ShouldNotContain("sk1");
    }

    [Fact]
    public async Task SyncShow_TypoProviderRow_PrintsRawValue_AndRoutesFieldsAsS3()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "minio",
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: minio");
        stdout.ShouldContain("endpoint: http://s3.example.com");
    }

    // ── sync add --cli (cloud-identity credential modes) ──

    [Fact]
    public async Task SyncAddAzure_CliMode_WritesAccountRow_NoPrompt()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, stderr) = await Run(
            ["sync", "add", "azure", "memories", "--cli", "--account", "myacct", "--object-key", "bank.db"], store);

        exit.ShouldBe(0);
        store.Settings["sync.provider"].ShouldBe("azure");
        store.Settings["sync.azureAccount"].ShouldBe("myacct");
        store.Settings["sync.container"].ShouldBe("memories");
        store.Settings["sync.objectKey"].ShouldBe("bank.db");
        store.Settings.ShouldNotContainKey("sync.connectionString");
        stdout.ShouldContain("(az CLI)");
        stderr.ShouldNotContain("connection string");
    }

    [Fact]
    public async Task SyncAddAzure_CliMode_MissingAccount_ReturnsError()
    {
        var store = new FakeConfigStore();

        var (exit, _, stderr) = await Run(["sync", "add", "azure", "memories", "--cli"], store);

        exit.ShouldBe(1);
        stderr.ShouldContain("--account is required with --cli");
        store.Settings.ShouldBeEmpty();
    }

    [Fact]
    public async Task SyncAddAzure_CliMode_ClearsStaleConnectionStringRow()
    {
        var store = new FakeConfigStore
        {
            Settings = { ["sync.connectionString"] = "connstr" }
        };

        var (exit, _, _) = await Run(
            ["sync", "add", "azure", "memories", "--cli", "--account", "myacct"], store);

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey("sync.connectionString");
        store.Settings["sync.azureAccount"].ShouldBe("myacct");
    }

    [Fact]
    public async Task SyncAddS3_CliMode_WritesChainRow_NoPrompt()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, stderr) = await Run(
        [
            "sync", "add", "s3", "http://s3.example.com", "--bucket", "memories",
            "--cli", "--region", "us-east-1", "--object-key", "bank.db"
        ], store);

        exit.ShouldBe(0);
        store.Settings["sync.provider"].ShouldBe("s3");
        store.Settings["sync.s3Chain"].ShouldBe("true");
        store.Settings["sync.endpoint"].ShouldBe("http://s3.example.com");
        store.Settings["sync.bucket"].ShouldBe("memories");
        store.Settings["sync.region"].ShouldBe("us-east-1");
        store.Settings["sync.objectKey"].ShouldBe("bank.db");
        store.Settings.ShouldNotContainKey("sync.accessKey");
        store.Settings.ShouldNotContainKey("sync.secretKey");
        stdout.ShouldContain("(AWS credential chain)");
        stderr.ShouldNotContain("access key");
    }

    [Fact]
    public async Task SyncAddS3_CliMode_ClearsAzureRows()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.connectionString"] = "connstr",
                ["sync.container"] = "memories",
                ["sync.azureAccount"] = "myacct"
            }
        };

        var (exit, _, _) = await Run(
            ["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories", "--cli"], store);

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey("sync.connectionString");
        store.Settings.ShouldNotContainKey("sync.container");
        store.Settings.ShouldNotContainKey("sync.azureAccount");
        store.Settings["sync.s3Chain"].ShouldBe("true");
    }

    [Fact]
    public async Task SyncAddS3_CliMode_ClearsStaleKeyRows()
    {
        var store = new FakeConfigStore
        {
            Settings = { ["sync.accessKey"] = "ak1", ["sync.secretKey"] = "sk1" }
        };

        var (exit, _, _) = await Run(
            ["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories", "--cli"], store);

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey("sync.accessKey");
        store.Settings.ShouldNotContainKey("sync.secretKey");
        store.Settings["sync.s3Chain"].ShouldBe("true");
    }

    [Fact]
    public async Task SyncAddAzure_ConnStringMode_ClearsStaleAccountRow()
    {
        var store = new FakeConfigStore
        {
            Settings = { ["sync.azureAccount"] = "myacct" }
        };

        var (exit, _, _) = await Run(["sync", "add", "azure", "memories"], store, new StringReader("connstr\n"));

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey("sync.azureAccount");
        store.Settings["sync.connectionString"].ShouldBe("connstr");
    }

    [Fact]
    public async Task SyncAddS3_KeyMode_ClearsStaleChainRow()
    {
        var store = new FakeConfigStore
        {
            Settings = { ["sync.s3Chain"] = "true" }
        };

        var (exit, _, _) = await Run(
            ["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories"], store,
            new StringReader("ak1\nsk1\n"));

        exit.ShouldBe(0);
        store.Settings.ShouldNotContainKey("sync.s3Chain");
        store.Settings["sync.accessKey"].ShouldBe("ak1");
        store.Settings["sync.secretKey"].ShouldBe("sk1");
    }

    [Fact]
    public async Task SyncShow_AzureAccountMode_PrintsAccountSetAndConnectionStringUnset()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.azureAccount"] = "myacct",
                ["sync.container"] = "memories"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: azure");
        stdout.ShouldContain("account: set");
        stdout.ShouldContain("connectionString: unset");
    }

    [Fact]
    public async Task SyncShow_AzureConnStringMode_PrintsAccountUnset()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.provider"] = "azure",
                ["sync.connectionString"] = "connstr",
                ["sync.container"] = "memories"
            }
        };

        var (_, stdout, _) = await Run(["sync", "show"], store);

        stdout.ShouldContain("account: unset");
        stdout.ShouldContain("connectionString: set");
    }

    [Fact]
    public async Task SyncShow_S3ChainMode_PrintsChainTrueAndKeysUnset()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.s3Chain"] = "true"
            }
        };

        var (exit, stdout, _) = await Run(["sync", "show"], store);

        exit.ShouldBe(0);
        stdout.ShouldContain("chain: true");
        stdout.ShouldContain("accessKey: unset");
        stdout.ShouldContain("secretKey: unset");
    }

    [Fact]
    public async Task SyncShow_S3KeyMode_PrintsChainFalse()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sync.endpoint"] = "http://s3.example.com",
                ["sync.bucket"] = "memories",
                ["sync.accessKey"] = "ak1",
                ["sync.secretKey"] = "sk1"
            }
        };

        var (_, stdout, _) = await Run(["sync", "show"], store);

        stdout.ShouldContain("chain: false");
        stdout.ShouldContain("accessKey: set");
    }
}
