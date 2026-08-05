using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     retrieval alpha, sweep threshold and sync config commands: settings-table writes
///     with validation, redacted key output, and the sweep-ttl removal (no ttl command).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ConfigCommandsRetrievalSweepSyncTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store,
        TextReader? stdin = null)
    {
        var parsed = CliArgs.Parse(args);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldNotBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await ConfigCommands.RunAsync(parsed.CommandPath, parsed.ParseResult, store, stdout, stderr,
            stdin ?? TextReader.Null, TestContext.Current.CancellationToken);
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
    public async Task SweepShow_NoRow_PrintsDefault()
    {
        var (exit, stdout, _) = await Run(["sweep", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("0.3");
    }

    [Fact]
    public async Task SweepShow_WithRow_PrintsRowValue()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["sweep.threshold"] = "0.1"
            }
        };

        var (_, stdout, _) = await Run(["sweep", "show"], store);

        stdout.Trim().ShouldBe("0.1");
    }

    // ── sync add / remove / show ──

    [Fact]
    public async Task SyncAddS3_WritesAllRows_IncludingSecrets_PromptedInteractively()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, stderr) = await Run(
            ["sync", "add", "s3", "http://s3.example.com",
                "--bucket", "memories", "--region", "us-east-1", "--object-key", "bank.db"], store,
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
}
