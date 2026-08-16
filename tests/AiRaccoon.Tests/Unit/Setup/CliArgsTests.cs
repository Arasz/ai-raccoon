using System.CommandLine;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Render;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Parser behavior for the ai-raccoon CLI surface (System.CommandLine 2.0.10): verb-style
///     config commands, launch-identity flags, removed-channel rejection, help/version
///     detection. A verb routes to config commands; no verb launches the server.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CliArgsTests
{
    [Fact]
    public void Parse_ParsesDataRootOption()
    {
        CliArgs.TryParse(["--data-root", "/x"], out var parsed);

        parsed!.Options.ShouldNotBeNull();
        parsed.Options.DataRoot.ShouldBe("/x");
        parsed.CommandPath.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ParsesPortOption()
    {
        CliArgs.TryParse(["--port", "7721"], out var parsed);

        parsed!.Options.ShouldNotBeNull();
        parsed.Options.Port.ShouldBe(7721);
        parsed.CommandPath.ShouldBeEmpty();
    }

    [Fact]
    public void CommandTree_RegistersOptionNamesExactlyOncePerCommand()
    {
        // A duplicate option name in the tree is accepted at build time, but System.CommandLine
        // 2.0.10 then fails symbol lookup for the whole parse result.
        var root = CliCommandTree.BuildFullRootCommand();

        var duplicates = new List<string>();

        void Collect(Command command)
        {
            duplicates.AddRange(command.Options
                .GroupBy(o => o.Name, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key));
            foreach (var sub in command.Subcommands)
            {
                Collect(sub);
            }
        }

        Collect(root);

        duplicates.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ParsesQuietFlag()
    {
        CliArgs.TryParse(["--quiet"], out var parsed);

        parsed!.Options.ShouldNotBeNull();
        parsed.Options.Quiet.ShouldBeTrue();
    }

    [Fact]
    public void Parse_NoQuietFlag_DefaultsToFalse()
    {
        CliArgs.TryParse([], out var parsed);

        parsed!.Options.Quiet.ShouldBeFalse();
    }

    [Fact]
    public void Parse_QuietExplicitFalse_StaysFalse()
    {
        CliArgs.TryParse(["--quiet=false"], out var parsed);

        parsed!.Options.Quiet.ShouldBeFalse();
    }

    [Fact]
    public void Parse_PortOnly_DoesNotShortcutToNullOptions()
    {
        CliArgs.TryParse(["--port", "0"], out var parsed);

        parsed!.Options.ShouldNotBeNull();
        parsed.Options.Port.ShouldBe(0);
    }

    [Fact]
    public void Parse_NoPortOption_UsesDefault()
    {
        CliArgs.TryParse(["--transport", "http"], out var parsed);

        parsed!.Options.ShouldNotBeNull();
        parsed.Options.Port.ShouldBe(7721);
    }

    [Fact]
    public void Parse_GarbagePort_ReturnsError()
    {
        CliArgs.TryParse(["--port", "banana"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_NoArgs_ReturnsDefaultOptionsAndNoCommand()
    {
        CliArgs.TryParse([], out var parsed);

        parsed!.CommandPath.ShouldBeEmpty();
        parsed.Errors.ShouldBeEmpty();
        parsed.ShowHelp.ShouldBeFalse();
        parsed.Options.Transport.ShouldBe(DefaultOptions.Transport);
        parsed.Options.Port.ShouldBe(DefaultOptions.Port);
        parsed.Options.IsPortExplicit.ShouldBeFalse();
        parsed.Options.IsTransportExplicit.ShouldBeFalse();
        parsed.Options.DataRoot.ShouldBe(DefaultOptions.DataRoot);
        parsed.Options.InstallScope.ShouldBe(DefaultOptions.InstallScope);
    }

    [Fact]
    public void Parse_TransportExplicit_IsTrueOnlyWhenFlagGiven()
    {
        CliArgs.TryParse(["--transport", "http"], out var with);
        with.ShouldNotBeNull().Options.IsTransportExplicit.ShouldBeTrue();

        CliArgs.TryParse(["serve"], out var without);
        without.ShouldNotBeNull().Options.IsTransportExplicit.ShouldBeFalse();
    }

    [Fact]
    public void Parse_LaunchFlagsOnly_StillLaunchesServer()
    {
        CliArgs.TryParse(["--transport", "http", "--install-scope", "project"], out var parsed);

        parsed!.CommandPath.ShouldBeEmpty();
        parsed.Errors.ShouldBeEmpty();
        parsed.Options.ShouldNotBeNull();
        parsed.Options.Transport.ShouldBe(McpTransport.Http);
    }


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
            CliArgs.TryParse(args, out var parsed);

            parsed!.Errors.ShouldNotBeEmpty();
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
            CliArgs.TryParse(args, out var parsed);

            parsed!.Errors.ShouldNotBeEmpty();
        }
    }


    [Fact]
    public void Parse_AccessDefaultSet_ParsesCommandPathAndMode()
    {
        CliArgs.TryParse(["settings", "access", "default", "set", "rw"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "access", "default", "set"]);
        parsed.ParsedCliArgs.GetValue<string>("mode").ShouldBe("rw");
    }

    [Fact]
    public void Parse_AccessDefaultShow_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "access", "default", "show"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "access", "default", "show"]);
    }

    [Fact]
    public void Parse_AccessSet_ParsesProjectIdAndMode()
    {
        CliArgs.TryParse(["settings", "access", "set", "acme", "full"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "access", "set"]);
        parsed.ParsedCliArgs.GetValue<string>("project-id").ShouldBe("acme");
        parsed.ParsedCliArgs.GetValue<string>("mode").ShouldBe("full");
    }

    [Fact]
    public void Parse_AccessSetStar_ParsesWildcardProjectId()
    {
        CliArgs.TryParse(["settings", "access", "set", "*", "ro"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.ParsedCliArgs.GetValue<string>("project-id").ShouldBe("*");
    }

    [Fact]
    public void Parse_AccessUnset_ParsesProjectId()
    {
        CliArgs.TryParse(["settings", "access", "unset", "acme"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "access", "unset"]);
        parsed.ParsedCliArgs.GetValue<string>("project-id").ShouldBe("acme");
    }

    [Fact]
    public void Parse_AccessList_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "access", "list"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "access", "list"]);
    }


    [Fact]
    public void Parse_ModelSetLocal_ParsesCommandPath()
    {
        CliArgs.TryParse(["model", "set", "local"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["model", "set", "local"]);
    }

    [Fact]
    public void Parse_ModelSetLocalWithPath_ParsesOptionalPath()
    {
        CliArgs.TryParse(["model", "set", "local", "/models/custom.onnx"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.ParsedCliArgs.GetValue<string>("path").ShouldBe("/models/custom.onnx");
    }

    [Fact]
    public void Parse_ModelSetOpenAi_ParsesModelAndOptionalBaseUrl()
    {
        CliArgs.TryParse(["model", "set", "openai", "text-embedding-3-small"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["model", "set", "openai"]);
        parsed.ParsedCliArgs.GetValue<string>("model").ShouldBe("text-embedding-3-small");
        parsed.ParsedCliArgs.GetValue<string>("base-url").ShouldBeNull();
    }

    [Fact]
    public void Parse_ModelSetOpenAiWithBaseUrlAndApiKey_ParsesOptions()
    {
        CliArgs.TryParse(
            ["model", "set", "openai", "m", "http://localhost:11434/v1", "--api-key", "k"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.ParsedCliArgs.GetValue<string>("base-url").ShouldBe("http://localhost:11434/v1");
        parsed.ParsedCliArgs.GetValue<string>("--api-key").ShouldBe("k");
    }

    [Fact]
    public void Parse_ModelReset_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "model", "reset"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "model", "reset"]);
    }

    [Fact]
    public void Parse_ModelShow_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "model", "show"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "model", "show"]);
    }


    [Fact]
    public void Parse_RetrievalAlphaSet_ParsesAlpha()
    {
        CliArgs.TryParse(["settings", "retrieval", "alpha", "set", "0.3"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "retrieval", "alpha", "set"]);
        parsed.ParsedCliArgs.GetValue<string>("alpha").ShouldBe("0.3");
    }

    [Fact]
    public void Parse_RetrievalAlphaShow_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "retrieval", "alpha", "show"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "retrieval", "alpha", "show"]);
    }

    [Fact]
    public void Parse_SweepThresholdSet_ParsesThreshold()
    {
        CliArgs.TryParse(["settings", "sweep", "threshold", "set", "0.3"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "sweep", "threshold", "set"]);
        parsed.ParsedCliArgs.GetValue<string>("threshold").ShouldBe("0.3");
    }

    [Fact]
    public void Parse_SweepShow_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "sweep", "show"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "sweep", "show"]);
    }

    [Fact]
    public void Parse_SweepTtl_IsNotACommand()
    {
        // The GLOBAL sweep.ttl_days knob is REMOVED, not moved — no ttl command exists.
        // Per-entry ttl_days is live data, set by the memory_set_ttl tool, never by the CLI.
        CliArgs.TryParse(["settings", "sweep", "ttl", "set", "30"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }


    [Fact]
    public void Parse_SyncAddS3_ParsesUrlAndOptions()
    {
        CliArgs.TryParse(
        [
            "settings", "sync", "add", "s3", "http://s3.example.com",
            "--bucket", "memories", "--region", "us-east-1", "--object-key", "bank.db"
        ], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "sync", "add", "s3"]);
        parsed.ParsedCliArgs.GetValue<string>("url").ShouldBe("http://s3.example.com");
        parsed.ParsedCliArgs.GetValue<string>("--bucket").ShouldBe("memories");
        parsed.ParsedCliArgs.GetValue<string>("--region").ShouldBe("us-east-1");
        parsed.ParsedCliArgs.GetValue<string>("--object-key").ShouldBe("bank.db");
    }

    [Fact]
    public void Parse_SyncAddS3_MissingBucket_ReturnsError()
    {
        CliArgs.TryParse(["settings", "sync", "add", "s3", "http://s3.example.com"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
        parsed.Errors.ShouldContain(e => e.Contains("--bucket"));
    }

    [Fact]
    public void Parse_SyncAddAzure_ParsesContainerAndOptions()
    {
        CliArgs.TryParse(["settings", "sync", "add", "azure", "memories", "--object-key", "bank.db"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "sync", "add", "azure"]);
        parsed.ParsedCliArgs.GetValue<string>("container").ShouldBe("memories");
        parsed.ParsedCliArgs.GetValue<string>("--object-key").ShouldBe("bank.db");
    }

    [Fact]
    public void Parse_SyncAddAzure_NoContainer_ReturnsError()
    {
        CliArgs.TryParse(["settings", "sync", "add", "azure"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_SyncAddAzure_CliMode_ParsesAccountOption()
    {
        CliArgs.TryParse(
            ["settings", "sync", "add", "azure", "memories", "--cli", "--account", "myacct", "--object-key", "bank.db"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "sync", "add", "azure"]);
        parsed.ParsedCliArgs.GetValue<bool>("--cli").ShouldBeTrue();
        parsed.ParsedCliArgs.GetValue<string>("--account").ShouldBe("myacct");
    }

    [Fact]
    public void Parse_SyncAddS3_CliMode_ParsesCliOption()
    {
        CliArgs.TryParse(
            ["settings", "sync", "add", "s3", "http://s3.example.com", "--bucket", "memories", "--cli"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "sync", "add", "s3"]);
        parsed.ParsedCliArgs.GetValue<bool>("--cli").ShouldBeTrue();
    }

    [Fact]
    public void Parse_SyncRemove_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "sync", "remove"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "sync", "remove"]);
    }

    [Fact]
    public void Parse_SyncShow_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "sync", "show"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "sync", "show"]);
    }


    [Fact]
    public void Parse_WatchEnable_ParsesTargetAndBoolean()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "acme", "true"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "watch", "enable"]);
        parsed.ParsedCliArgs.GetValue<string>("target").ShouldBe("acme");
        parsed.ParsedCliArgs.GetValue<bool>("enabled").ShouldBeTrue();
    }

    [Fact]
    public void Parse_WatchDisableStar_ParsesWildcardTarget()
    {
        CliArgs.TryParse(["settings", "watch", "disable", "*", "false"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "watch", "disable"]);
        parsed.ParsedCliArgs.GetValue<string>("target").ShouldBe("*");
    }

    [Fact]
    public void Parse_WatchEnable_InvalidBoolean_ReturnsError()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "acme", "maybe"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_WatchScopeAdd_ParsesTargetAndPath()
    {
        CliArgs.TryParse(["settings", "ingest", "scope", "add", "acme", "/a/b"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "ingest", "scope", "add"]);
        parsed.ParsedCliArgs.GetValue<string>("target").ShouldBe("acme");
        parsed.ParsedCliArgs.GetValue<string>("path").ShouldBe("/a/b");
    }

    [Fact]
    public void Parse_WatchScopeRemove_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "ingest", "scope", "remove", "*", "/a"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "ingest", "scope", "remove"]);
    }

    [Fact]
    public void Parse_WatchScopeList_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "ingest", "scope", "list", "acme"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "ingest", "scope", "list"]);
    }

    [Fact]
    public void Parse_WatchConcurrency_ParsesTargetAndValue()
    {
        CliArgs.TryParse(["settings", "watch", "concurrency", "acme", "8"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "watch", "concurrency"]);
        parsed.ParsedCliArgs.GetValue<int>("value").ShouldBe(8);
    }

    [Fact]
    public void Parse_WatchConcurrency_NonNumeric_ReturnsError()
    {
        CliArgs.TryParse(["settings", "watch", "concurrency", "acme", "many"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_WatchList_ParsesCommandPath()
    {
        CliArgs.TryParse(["settings", "watch", "list"], out var parsed);
        parsed!.CommandPath.ShouldBe(["settings", "watch", "list"]);
    }

    [Fact]
    public void Parse_WatchRegistered_ParsesCommandPath()
    {
        CliArgs.TryParse(["watch", "registered"], out var parsed);
        parsed!.CommandPath.ShouldBe(["watch", "registered"]);
    }

    [Fact]
    public void Parse_WatchRegistered_AcceptsOptionalProjectFilter()
    {
        CliArgs.TryParse(["watch", "registered", "acme"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["watch", "registered"]);
        parsed.ParsedCliArgs.GetValue<string?>("project-id").ShouldBe("acme");
    }

    [Fact]
    public void Parse_WatchRemove_ParsesTarget()
    {
        CliArgs.TryParse(["settings", "watch", "remove", "acme"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "watch", "remove"]);
        parsed.ParsedCliArgs.GetValue<string>("target").ShouldBe("acme");
    }


    [Fact]
    public void Parse_BareServe_StillParsesCleanly_AfterObservabilitySubcommandAdded()
    {
        // An "observability" subcommand under serve makes System.CommandLine require one
        // unless serve declares its own action.
        CliArgs.TryParse(["serve"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
    }

    [Fact]
    public void Parse_ServeObservability_YieldsTheObservabilityCommandPath()
    {
        CliArgs.TryParse(["serve", "observability", "counters"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve", "observability"]);
    }

    [Fact]
    public void Parse_ServeObservability_ReadsPortAfterTheKind()
    {
        CliArgs.TryParse(["serve", "observability", "counters", "--port", "9000"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ObservabilityPortOption).ShouldBe(9000);
    }

    [Fact]
    public void Parse_ServeObservability_ReadsPortBeforeTheKind()
    {
        CliArgs.TryParse(["serve", "observability", "--port", "9000", "counters"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ObservabilityPortOption).ShouldBe(9000);
    }

    [Fact]
    public void Parse_ServeObservability_DefaultsToPort7721()
    {
        CliArgs.TryParse(["serve", "observability", "trace"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ObservabilityPortOption).ShouldBe(7721);
    }

    [Fact]
    public void Parse_ServeObservabilityPid_IsAccepted()
    {
        CliArgs.TryParse(["serve", "observability", "pid"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve", "observability"]);
    }

    [Fact]
    public void Parse_ServeObservabilityUnknownKind_IsAParseError()
    {
        CliArgs.TryParse(["serve", "observability", "bogus"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_ServeObservabilityPortZero_IsAParseError()
    {
        CliArgs.TryParse(["serve", "observability", "counters", "--port", "0"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_ServeHelp_StillRendersHelp()
    {
        CliArgs.TryParse(["serve", "--help"], out var parsed);

        parsed!.ShowHelp.ShouldBeTrue();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ServeAlone_ParsesCommandPathAndDefaultFormat()
    {
        CliArgs.TryParse(["serve"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ServeFormatOption).ShouldBe("hermes");
    }

    [Fact]
    public void Parse_Serve_ParsesServeOptions()
    {
        CliArgs.TryParse(
            ["serve", "--port", "0", "--idle-timeout", "30m", "--mcp-entry", "--format", "claude"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ServePortOption).ShouldBe(0);
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ServeIdleTimeoutOption).ShouldBe("30m");
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ServeMcpEntryOption).ShouldBeTrue();
        parsed.ParsedCliArgs.GetValue(CliCommandTree.ServeFormatOption).ShouldBe("claude");
    }

    [Fact]
    public void Parse_ServeInvalidIdleTimeout_ReturnsError()
    {
        CliArgs.TryParse(["serve", "--idle-timeout", "4x"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_ServeInvalidFormat_ReturnsError()
    {
        CliArgs.TryParse(["serve", "--mcp-entry", "--format", "bogus"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_ServeHelp_ListsServeOptionsAndRecipe()
    {
        var writer = new StringWriter();

        CliArgs.TryParse(["serve", "--help"], out var parsed);
        parsed!.RenderTo(new StandardStreams(TextReader.Null, TextWriter.Null, writer));

        var help = writer.ToString();
        help.ShouldContain("--port");
        help.ShouldContain("--idle-timeout");
        help.ShouldContain("--mcp-entry");
        help.ShouldContain("--format");
        help.ShouldContain("ai-raccoon serve > serve.log 2>&1 &");
        help.ShouldContain("always HTTP");
    }


    [Fact]
    public void Parse_RootOptionBeforeVerb_ParsesForBankResolution()
    {
        CliArgs.TryParse(["--data-root", "/x", "settings", "access", "list"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["settings", "access", "list"]);
        parsed.Options.ShouldNotBeNull();
        parsed.Options.DataRoot.ShouldBe("/x");
    }

    [Fact]
    public void Parse_RootOptionAfterVerb_ReturnsError()
    {
        // Pinned against System.CommandLine 2.0.10: root options are not recognized
        // after a subcommand; launch flags must precede the verb.
        CliArgs.TryParse(["settings", "access", "list", "--data-root", "/x"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }


    [Fact]
    public void Parse_UnknownOption_ReturnsError()
    {
        CliArgs.TryParse(["--bogus"], out var parsed);

        parsed!.Errors.ShouldHaveSingleItem().ShouldContain("--bogus");
    }

    [Fact]
    public void Parse_MissingValue_ReturnsError()
    {
        CliArgs.TryParse(["--data-root"], out var parsed);

        parsed!.Errors.ShouldHaveSingleItem();
    }

    [Fact]
    public void Parse_InvalidEnumValue_ReturnsError()
    {
        CliArgs.TryParse(["--transport", "ftp"], out var parsed);

        parsed!.Errors.ShouldHaveSingleItem().ShouldContain("--transport");
    }

    /// <summary>
    ///     H1: a verb after the bad top-level option changes System.CommandLine's error-eagerness
    ///     — `--transport`/`--install-scope` (custom-enum Options) only surface a type-coercion
    ///     failure through `parseResult.Errors` when there is no matched subcommand; with one
    ///     present the failure is silent until `GetValueOrDefault&lt;T&gt;()` is actually called,
    ///     and that call throws rather than recording an error. `--port` (a built-in `int`) does
    ///     not have this gap. Pinned by reading System.CommandLine 2.0.10's raw ParseResult directly.
    /// </summary>
    [Theory]
    [InlineData("--install-scope")]
    [InlineData("--transport")]
    [InlineData("--port")]
    public void Parse_BadTopLevelOptionValueWithVerb_NeverDiscardsDataRootOnSuccess(string badOption)
    {
        var ok = CliArgs.TryParse(["--data-root", "/tmp/x", badOption, "garbage", "access", "default", "show"], out var parsed);

        if (ok)
        {
            parsed!.Options.DataRoot.ShouldBe("/tmp/x");
        }
        else
        {
            parsed!.Errors.ShouldNotBeEmpty();
        }
    }

    [Fact]
    public void Parse_BadInstallScopeWithVerb_RefusesWithNonZeroExit()
    {
        var ok = CliArgs.TryParse(["--data-root", "/tmp/x", "--install-scope", "garbage", "access", "default", "show"], out var parsed);

        ok.ShouldBeFalse();
        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_BadTransportWithVerb_RefusesWithNonZeroExit()
    {
        var ok = CliArgs.TryParse(["--data-root", "/tmp/x", "--transport", "garbage", "access", "default", "show"], out var parsed);

        ok.ShouldBeFalse();
        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_BadPortWithVerb_RefusesWithNonZeroExit()
    {
        var ok = CliArgs.TryParse(["--data-root", "/tmp/x", "--port", "garbage", "access", "default", "show"], out var parsed);

        ok.ShouldBeFalse();
        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_BadInstallScopeWithVerbNoDataRoot_Refuses()
    {
        // No --data-root in play at all: still must not silently succeed with defaults.
        var ok = CliArgs.TryParse(["--install-scope", "garbage", "access", "default", "show"], out var parsed);

        ok.ShouldBeFalse();
        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_ValidTopLevelOptionsWithVerb_StillParseCleanly()
    {
        var ok = CliArgs.TryParse(["--data-root", "/tmp/x", "--install-scope", "project", "settings", "access", "default", "show"], out var parsed);

        ok.ShouldBeTrue();
        parsed!.Errors.ShouldBeEmpty();
        parsed.Options.DataRoot.ShouldBe("/tmp/x");
        parsed.Options.InstallScope.ShouldBe(InstallScope.Project);
    }

    [Fact]
    public void Parse_Help_ReturnsShowHelp()
    {
        CliArgs.TryParse(["--help"], out var parsed);

        parsed!.ShowHelp.ShouldBeTrue();
        parsed.ShowVersion.ShouldBeFalse();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_Version_ReturnsShowVersion()
    {
        CliArgs.TryParse(["--version"], out var parsed);

        parsed!.ShowVersion.ShouldBeTrue();
        parsed.ShowHelp.ShouldBeFalse();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_HelpAndBogus_BehaviorPinned()
    {
        CliArgs.TryParse(["--help", "--bogus"], out var parsed);

        parsed!.ShowHelp.ShouldBeTrue();
        parsed.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_DuplicateOption_ReturnsError()
    {
        CliArgs.TryParse(["--data-root", "/a", "--data-root", "/b"], out var parsed);

        parsed!.Errors.ShouldHaveSingleItem().ShouldContain("--data-root");
    }

    [Fact]
    public void Parse_HostBootstrapFlags_AreAcceptedAndIgnored()
    {
        CliArgs.TryParse(
            ["--environment=Development", "--contentRoot=/tmp/x", "--applicationName=AiRaccoon"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_VerbWithoutSubcommand_ReturnsError()
    {
        CliArgs.TryParse(["settings", "access"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }


    [Fact]
    public void Parse_EncryptionBitwarden_ParsesCommandPath()
    {
        CliArgs.TryParse(["encryption", "bitwarden"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["encryption", "bitwarden"]);
        parsed.ParsedCliArgs.GetResult("-t").ShouldBeNull();
    }

    [Fact]
    public void Parse_EncryptionBitwardenWithToken_ParsesToken()
    {
        CliArgs.TryParse(["encryption", "bitwarden", "-t", "tok-123"], out var parsed);

        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["encryption", "bitwarden"]);
        parsed.ParsedCliArgs.GetValue<string>("-t").ShouldBe("tok-123");
    }

    [Fact]
    public void Parse_EncryptionShow_ParsesCommandPath()
    {
        CliArgs.TryParse(["encryption", "show"], out var parsed);
        parsed!.CommandPath.ShouldBe(["encryption", "show"]);
    }

    [Fact]
    public void Parse_EncryptionUnset_ParsesCommandPath()
    {
        CliArgs.TryParse(["encryption", "unset"], out var parsed);
        parsed!.CommandPath.ShouldBe(["encryption", "unset"]);
    }

    [Fact]
    public void Parse_EncryptionUnknownSubcommand_ReturnsError()
    {
        CliArgs.TryParse(["encryption", "rotate"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void Render_Help_ListsEncryptionVerb()
    {
        var writer = new StringWriter();

        CliArgs.TryParse(["--help"], out var parsed);
        parsed!.RenderTo(new StandardStreams(TextReader.Null, TextWriter.Null, writer));

        writer.ToString().ShouldContain("encryption");
    }

    /// <summary>
    ///     The --transport help name is derived from McpTransport, not hand-written: a transport
    ///     the enum gains but the help name never mentions fails here rather than shipping a lie.
    /// </summary>
    [Fact]
    public void Render_Help_ListsTheProxyTransport()
    {
        var writer = new StringWriter();

        CliArgs.TryParse(["--help"], out var parsed);
        parsed!.RenderTo(new StandardStreams(TextReader.Null, TextWriter.Null, writer));

        var help = writer.ToString();
        help.ShouldContain("proxy");
        help.ShouldContain(string.Join('|', Enum.GetNames<McpTransport>().Select(name => name.ToLowerInvariant())));
    }
}
