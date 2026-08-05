using AiRaccoon.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Parser behavior for the ai-raccoon CLI surface (System.CommandLine 2.0.10 GA):
///     verb-style config command tree, launch-identity flags, removed-channel rejection,
///     help/version detection. A verb in the args routes to config commands; no verb
///     (with or without launch flags) launches the server.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CliArgsTests
{
    // ── Launch identity ──

    [Fact]
    public void Parse_ParsesDataRootOption()
    {
        var parsed = CliArgs.Parse(["--data-root", "/x"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.DataRoot.ShouldBe("/x");
        parsed.CommandPath.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ParsesPortOption()
    {
        var parsed = CliArgs.Parse(["--port", "7721"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.Port.ShouldBe(7721);
        parsed.CommandPath.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_PortOnly_DoesNotShortcutToNullOptions()
    {
        var parsed = CliArgs.Parse(["--port", "0"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.Port.ShouldBe(0);
    }

    [Fact]
    public void Parse_NoPortOption_UsesDefault()
    {
        var parsed = CliArgs.Parse(["--transport", "http"]);

        parsed.Options.ShouldNotBeNull();
        parsed.Options.Port.ShouldBe(7721);
    }

    [Fact]
    public void Parse_GarbagePort_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--port", "banana"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_NoArgs_ReturnsNullOptionsAndNoCommand()
    {
        var parsed = CliArgs.Parse([]);

        parsed.Options.ShouldBeNull();
        parsed.CommandPath.ShouldBeEmpty();
        parsed.Errors.ShouldBeEmpty();
        parsed.ShowHelp.ShouldBeFalse();
    }

    [Fact]
    public void Parse_LaunchFlagsOnly_StillLaunchesServer()
    {
        var parsed = CliArgs.Parse(["--transport", "http", "--install-scope", "project"]);

        parsed.CommandPath.ShouldBeEmpty();
        parsed.Errors.ShouldBeEmpty();
        parsed.Options.ShouldNotBeNull();
        parsed.Options.Transport.ShouldBe("http");
    }

    // ── Removed channels are rejected ──

    [Fact]
    public void Parse_RemovedRuntimeFlags_ReturnError()
    {
        // The single-channel refactor deleted these launch flags; the parser's
        // unknown-option error is the guard.
        foreach (var args in new[]
                 {
                     new[] { "--access-mode", "ro" },
                     new[] { "--embedding-model", "/m.onnx" },
                     new[] { "--sync-endpoint", "http://s3" },
                     new[] { "--sync-bucket", "b" },
                     new[] { "--sync-region", "r" },
                     new[] { "--sync-object-key", "k" }
                 })
        {
            var parsed = CliArgs.Parse(args);

            parsed.Options.ShouldBeNull();
            parsed.Errors.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Parse_SecretFlagsNeverDeclared_ReturnError()
    {
        // Secrets are never CLI options: the parser's unknown-option error is the defense.
        foreach (var args in new[]
                 {
                     new[] { "--sync-access-key", "x" },
                     new[] { "--sync-secret-key", "x" },
                     new[] { "--sync-connection-string", "x" },
                     new[] { "--db-passphrase", "x" },
                     new[] { "--openai-api-key", "x" }
                 })
        {
            var parsed = CliArgs.Parse(args);

            parsed.Options.ShouldBeNull();
            parsed.Errors.ShouldNotBeEmpty();
        }
    }

    // ── Verb tree: access ──

    [Fact]
    public void Parse_AccessDefaultSet_ParsesCommandPathAndMode()
    {
        var parsed = CliArgs.Parse(["access", "default", "set", "rw"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["access", "default", "set"]);
        parsed.ParseResult.GetValue<string>("mode").ShouldBe("rw");
    }

    [Fact]
    public void Parse_AccessDefaultShow_ParsesCommandPath()
    {
        var parsed = CliArgs.Parse(["access", "default", "show"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["access", "default", "show"]);
    }

    [Fact]
    public void Parse_AccessSet_ParsesProjectIdAndMode()
    {
        var parsed = CliArgs.Parse(["access", "set", "acme", "full"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["access", "set"]);
        parsed.ParseResult.GetValue<string>("project-id").ShouldBe("acme");
        parsed.ParseResult.GetValue<string>("mode").ShouldBe("full");
    }

    [Fact]
    public void Parse_AccessSetStar_ParsesWildcardProjectId()
    {
        var parsed = CliArgs.Parse(["access", "set", "*", "ro"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.ParseResult.GetValue<string>("project-id").ShouldBe("*");
    }

    [Fact]
    public void Parse_AccessUnset_ParsesProjectId()
    {
        var parsed = CliArgs.Parse(["access", "unset", "acme"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["access", "unset"]);
        parsed.ParseResult.GetValue<string>("project-id").ShouldBe("acme");
    }

    [Fact]
    public void Parse_AccessList_ParsesCommandPath()
    {
        var parsed = CliArgs.Parse(["access", "list"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["access", "list"]);
    }

    // ── Verb tree: model ──

    [Fact]
    public void Parse_ModelSetLocal_ParsesCommandPath()
    {
        var parsed = CliArgs.Parse(["model", "set", "local"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["model", "set", "local"]);
    }

    [Fact]
    public void Parse_ModelSetLocalWithPath_ParsesOptionalPath()
    {
        var parsed = CliArgs.Parse(["model", "set", "local", "/models/custom.onnx"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.ParseResult.GetValue<string>("path").ShouldBe("/models/custom.onnx");
    }

    [Fact]
    public void Parse_ModelSetOpenAi_ParsesModelAndOptionalBaseUrl()
    {
        var parsed = CliArgs.Parse(["model", "set", "openai", "text-embedding-3-small"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["model", "set", "openai"]);
        parsed.ParseResult.GetValue<string>("model").ShouldBe("text-embedding-3-small");
        parsed.ParseResult.GetValue<string>("base-url").ShouldBeNull();
    }

    [Fact]
    public void Parse_ModelSetOpenAiWithBaseUrlAndApiKey_ParsesOptions()
    {
        var parsed = CliArgs.Parse(
            ["model", "set", "openai", "m", "http://localhost:11434/v1", "--api-key", "k"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.ParseResult.GetValue<string>("base-url").ShouldBe("http://localhost:11434/v1");
        parsed.ParseResult.GetValue<string>("--api-key").ShouldBe("k");
    }

    [Fact]
    public void Parse_ModelReset_ParsesCommandPath() => CliArgs.Parse(["model", "reset"]).CommandPath.ShouldBe(["model", "reset"]);

    [Fact]
    public void Parse_ModelShow_ParsesCommandPath() => CliArgs.Parse(["model", "show"]).CommandPath.ShouldBe(["model", "show"]);

    // ── Verb tree: retrieval / sweep ──

    [Fact]
    public void Parse_RetrievalAlphaSet_ParsesAlpha()
    {
        var parsed = CliArgs.Parse(["retrieval", "alpha", "set", "0.3"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["retrieval", "alpha", "set"]);
        parsed.ParseResult.GetValue<string>("alpha").ShouldBe("0.3");
    }

    [Fact]
    public void Parse_RetrievalAlphaShow_ParsesCommandPath() =>
        CliArgs.Parse(["retrieval", "alpha", "show"]).CommandPath.ShouldBe(["retrieval", "alpha", "show"]);

    [Fact]
    public void Parse_SweepThresholdSet_ParsesThreshold()
    {
        var parsed = CliArgs.Parse(["sweep", "threshold", "set", "0.3"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["sweep", "threshold", "set"]);
        parsed.ParseResult.GetValue<string>("threshold").ShouldBe("0.3");
    }

    [Fact]
    public void Parse_SweepShow_ParsesCommandPath() => CliArgs.Parse(["sweep", "show"]).CommandPath.ShouldBe(["sweep", "show"]);

    [Fact]
    public void Parse_SweepTtl_IsNotACommand()
    {
        // Ruling: sweep ttl_days is REMOVED, not moved — no ttl command exists.
        var parsed = CliArgs.Parse(["sweep", "ttl", "set", "30"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    // ── Verb tree: sync ──

    [Fact]
    public void Parse_SyncAddS3_ParsesUrlAndOptions()
    {
        var parsed = CliArgs.Parse(
            ["sync", "add", "s3", "http://s3.example.com",
                "--bucket", "memories", "--region", "us-east-1", "--object-key", "bank.db"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["sync", "add", "s3"]);
        parsed.ParseResult.GetValue<string>("url").ShouldBe("http://s3.example.com");
        parsed.ParseResult.GetValue<string>("--bucket").ShouldBe("memories");
        parsed.ParseResult.GetValue<string>("--region").ShouldBe("us-east-1");
        parsed.ParseResult.GetValue<string>("--object-key").ShouldBe("bank.db");
    }

    [Fact]
    public void Parse_SyncAddS3_MissingBucket_ReturnsError()
    {
        var parsed = CliArgs.Parse(["sync", "add", "s3", "http://s3.example.com"]);

        parsed.Errors.ShouldNotBeEmpty();
        parsed.Errors.ShouldContain(e => e.Contains("--bucket"));
    }

    [Fact]
    public void Parse_SyncAddAzure_ParsesContainerAndOptions()
    {
        var parsed = CliArgs.Parse(["sync", "add", "azure", "memories", "--object-key", "bank.db"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["sync", "add", "azure"]);
        parsed.ParseResult.GetValue<string>("container").ShouldBe("memories");
        parsed.ParseResult.GetValue<string>("--object-key").ShouldBe("bank.db");
    }

    [Fact]
    public void Parse_SyncAddAzure_NoContainer_ReturnsError()
    {
        var parsed = CliArgs.Parse(["sync", "add", "azure"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_SyncAddAzure_CliMode_ParsesAccountOption()
    {
        var parsed = CliArgs.Parse(
            ["sync", "add", "azure", "memories", "--cli", "--account", "myacct", "--object-key", "bank.db"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["sync", "add", "azure"]);
        parsed.ParseResult.GetValue<bool>("--cli").ShouldBeTrue();
        parsed.ParseResult.GetValue<string>("--account").ShouldBe("myacct");
    }

    [Fact]
    public void Parse_SyncAddS3_CliMode_ParsesCliOption()
    {
        var parsed = CliArgs.Parse(
            ["sync", "add", "s3", "http://s3.example.com", "--bucket", "memories", "--cli"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["sync", "add", "s3"]);
        parsed.ParseResult.GetValue<bool>("--cli").ShouldBeTrue();
    }

    [Fact]
    public void Parse_SyncRemove_ParsesCommandPath() => CliArgs.Parse(["sync", "remove"]).CommandPath.ShouldBe(["sync", "remove"]);

    [Fact]
    public void Parse_SyncShow_ParsesCommandPath() => CliArgs.Parse(["sync", "show"]).CommandPath.ShouldBe(["sync", "show"]);

    // ── Verb tree: watch ──

    [Fact]
    public void Parse_WatchEnable_ParsesTargetAndBoolean()
    {
        var parsed = CliArgs.Parse(["watch", "enable", "acme", "true"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "enable"]);
        parsed.ParseResult.GetValue<string>("target").ShouldBe("acme");
        parsed.ParseResult.GetValue<bool>("enabled").ShouldBeTrue();
    }

    [Fact]
    public void Parse_WatchDisableStar_ParsesWildcardTarget()
    {
        var parsed = CliArgs.Parse(["watch", "disable", "*", "false"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "disable"]);
        parsed.ParseResult.GetValue<string>("target").ShouldBe("*");
    }

    [Fact]
    public void Parse_WatchEnable_InvalidBoolean_ReturnsError()
    {
        var parsed = CliArgs.Parse(["watch", "enable", "acme", "maybe"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_WatchScopeAdd_ParsesTargetAndPath()
    {
        var parsed = CliArgs.Parse(["watch", "scope", "add", "acme", "/a/b"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "scope", "add"]);
        parsed.ParseResult.GetValue<string>("target").ShouldBe("acme");
        parsed.ParseResult.GetValue<string>("path").ShouldBe("/a/b");
    }

    [Fact]
    public void Parse_WatchScopeRemove_ParsesCommandPath() =>
        CliArgs.Parse(["watch", "scope", "remove", "*", "/a"]).CommandPath.ShouldBe(["watch", "scope", "remove"]);

    [Fact]
    public void Parse_WatchScopeList_ParsesCommandPath() =>
        CliArgs.Parse(["watch", "scope", "list", "acme"]).CommandPath.ShouldBe(["watch", "scope", "list"]);

    [Fact]
    public void Parse_WatchConcurrency_ParsesTargetAndValue()
    {
        var parsed = CliArgs.Parse(["watch", "concurrency", "acme", "8"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "concurrency"]);
        parsed.ParseResult.GetValue<int>("value").ShouldBe(8);
    }

    [Fact]
    public void Parse_WatchConcurrency_NonNumeric_ReturnsError()
    {
        var parsed = CliArgs.Parse(["watch", "concurrency", "acme", "many"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_WatchList_ParsesCommandPath() => CliArgs.Parse(["watch", "list"]).CommandPath.ShouldBe(["watch", "list"]);

    [Fact]
    public void Parse_WatchRegistered_ParsesCommandPath() => CliArgs.Parse(["watch", "registered"]).CommandPath.ShouldBe(["watch", "registered"]);

    [Fact]
    public void Parse_WatchRegistered_AcceptsOptionalProjectFilter()
    {
        var parsed = CliArgs.Parse(["watch", "registered", "acme"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "registered"]);
        parsed.ParseResult.GetValue<string?>("project-id").ShouldBe("acme");
    }

    [Fact]
    public void Parse_WatchRemove_ParsesTarget()
    {
        var parsed = CliArgs.Parse(["watch", "remove", "acme"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "remove"]);
        parsed.ParseResult.GetValue<string>("target").ShouldBe("acme");
    }

    // ── Option placement ──

    [Fact]
    public void Parse_RootOptionBeforeVerb_ParsesForBankResolution()
    {
        var parsed = CliArgs.Parse(["--data-root", "/x", "access", "list"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["access", "list"]);
        parsed.Options.ShouldNotBeNull();
        parsed.Options.DataRoot.ShouldBe("/x");
    }

    [Fact]
    public void Parse_RootOptionAfterVerb_ReturnsError()
    {
        // Pinned against System.CommandLine 2.0.10: root options are not recognized
        // after a subcommand; launch flags must precede the verb.
        var parsed = CliArgs.Parse(["access", "list", "--data-root", "/x"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    // ── Core parse behaviors (unchanged from the pre-verb surface) ──

    [Fact]
    public void Parse_UnknownOption_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--bogus"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("--bogus");
    }

    [Fact]
    public void Parse_MissingValue_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--data-root"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_InvalidEnumValue_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--transport", "ftp"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("--transport");
    }

    [Fact]
    public void Parse_Help_ReturnsShowHelp()
    {
        var parsed = CliArgs.Parse(["--help"]);

        parsed.ShowHelp.ShouldBeTrue();
        parsed.ShowVersion.ShouldBeFalse();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_Version_ReturnsShowVersion()
    {
        var parsed = CliArgs.Parse(["--version"]);

        parsed.ShowVersion.ShouldBeTrue();
        parsed.ShowHelp.ShouldBeFalse();
        parsed.Errors.ShouldBeEmpty();
        parsed.Options.ShouldBeNull();
    }

    [Fact]
    public void Parse_HelpAndBogus_BehaviorPinned()
    {
        var parsed = CliArgs.Parse(["--help", "--bogus"]);

        parsed.ShowHelp.ShouldBeTrue();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_DuplicateOption_ReturnsError()
    {
        var parsed = CliArgs.Parse(["--data-root", "/a", "--data-root", "/b"]);

        parsed.Options.ShouldBeNull();
        parsed.Errors.ShouldHaveSingleItem().ShouldContain("--data-root");
    }

    [Fact]
    public void Parse_HostBootstrapFlags_AreAcceptedAndIgnored()
    {
        var parsed = CliArgs.Parse(
            ["--environment=Development", "--contentRoot=/tmp/x", "--applicationName=AiRaccoon"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.Options.ShouldBeNull();
        parsed.CommandPath.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_VerbWithoutSubcommand_ReturnsError()
    {
        var parsed = CliArgs.Parse(["access"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    // ── Verb tree: encryption ──

    [Fact]
    public void Parse_EncryptionBitwarden_ParsesCommandPath()
    {
        var parsed = CliArgs.Parse(["encryption", "bitwarden"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["encryption", "bitwarden"]);
        parsed.ParseResult.GetResult("-t").ShouldBeNull();
    }

    [Fact]
    public void Parse_EncryptionBitwardenWithToken_ParsesToken()
    {
        var parsed = CliArgs.Parse(["encryption", "bitwarden", "-t", "tok-123"]);

        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["encryption", "bitwarden"]);
        parsed.ParseResult.GetValue<string>("-t").ShouldBe("tok-123");
    }

    [Fact]
    public void Parse_EncryptionShow_ParsesCommandPath() =>
        CliArgs.Parse(["encryption", "show"]).CommandPath.ShouldBe(["encryption", "show"]);

    [Fact]
    public void Parse_EncryptionUnset_ParsesCommandPath() =>
        CliArgs.Parse(["encryption", "unset"]).CommandPath.ShouldBe(["encryption", "unset"]);

    [Fact]
    public void Parse_EncryptionUnknownSubcommand_ReturnsError()
    {
        var parsed = CliArgs.Parse(["encryption", "rotate"]);

        parsed.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_Help_ListsEncryptionVerb()
    {
        var writer = new StringWriter();

        var exit = CliArgs.Render(CliArgs.Parse(["--help"]), writer);

        exit.ShouldBe(0);
        writer.ToString().ShouldContain("encryption");
    }
}
