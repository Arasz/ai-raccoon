using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Sync;
using AiRaccoon.Core.Watch;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Tools;
using Dapper;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Integration.Mcp;

/// <summary>
///     A correct refusal (path-outside-scope) must not read as a crash: the SDK logs Error on
///     every exception escaping a tool, so the old catch-and-rethrow shape produced an Error
///     record for an expected refusal.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolRefusalsTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("tool-refusals-tests");
    private bool _holdsEnvGate;

    /// <summary>
    ///     Takes <see cref="TestData.EnvVarGate" /> as a READER (docs/adr/0062). The gate was built
    ///     to serialise the classes that mutate the process-global AIRACCOON_DB_PASSPHRASE against
    ///     each other; it does nothing for a class that merely *reads* the environment by opening a
    ///     bank. This class stands up a real server, so an env mutation landing mid-run makes it open
    ///     a plain bank with a key — SQLite error 26, "file is not a database" — which is what its
    ///     intermittent CI reds turned out to be.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        _holdsEnvGate = true;
    }

    public ValueTask DisposeAsync()
    {
        DeleteRoot();
        if (_holdsEnvGate)
        {
            _holdsEnvGate = false;
            TestData.EnvVarGate.Release();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Only path-outside-scope was ever proven through a real McpServer; access-denied (project
    ///     set read-only, then a write tool) and unknown-workspace (unknown workspaceId) now are too.
    ///     Sync prefixes are skipped here — they need real cloud config, not just a local bank.
    /// </summary>
    public static TheoryData<string, Dictionary<string, object?>, string, string?, string?> RealServerRefusalCases =>
        new()
        {
            {
                "memory_write",
                new Dictionary<string, object?> { ["projectId"] = "acme-ro", ["content"] = "x" },
                "access-denied",
                "acme-ro",
                "ro"
            },
            {
                "memory_write",
                new Dictionary<string, object?> { ["projectId"] = "acme", ["content"] = "x", ["workspaceId"] = "ws-bogus" },
                "unknown-workspace",
                null,
                null
            },
            {
                // D1: 'keep' is documented as accepting the scalar "all", but the schema declares
                // string[] — the SDK's argument marshaller throws a raw JsonException instead of a
                // typed refusal (docs/reference/agent-memory-server.md Error shapes).
                "memory_workspace_consolidate",
                new Dictionary<string, object?> { ["projectId"] = "acme", ["workspaceId"] = "ws-1", ["keep"] = "all" },
                "invalid-argument",
                null,
                null
            },
            {
                "memory_share",
                new Dictionary<string, object?>
                {
                    ["projectId"] = "acme", ["hash"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd"
                },
                "unknown-hash",
                null,
                null
            },
            {
                // Binding-time failure: 'value' isn't a declared parameter, so the required 'content'
                // is missing and the SDK's reflection marshaller throws a plain ArgumentException.
                "memory_write",
                new Dictionary<string, object?> { ["projectId"] = "acme", ["value"] = "x" },
                "invalid-argument",
                null,
                null
            },
            {
                // In-body guard-clause failure: ArgumentException.ThrowIfNullOrWhiteSpace(hash) in
                // MemoryTools.cs. Delete requires the "full" access mode, so the project is seeded
                // that way — otherwise the access-denied check fires first and the guard clause is
                // never reached.
                "memory_delete",
                new Dictionary<string, object?> { ["projectId"] = "acme-full", ["hash"] = "" },
                "invalid-argument",
                "acme-full",
                "full"
            }
        };

    public static TheoryData<Exception, string> MappedRefusals =>
        new()
        {
            { new PathOutsideScopeException("/etc"), "path-outside-scope" },
            { new PathNotFoundException("/missing"), "path-not-found" },
            { new UnknownWorkspaceException("ws-1", "acme"), "unknown-workspace" },
            { new WatchDisabledException("acme"), "watching-disabled" },
            { new SyncNotConfiguredException(), "sync-not-configured" },
            { new SyncAuthFailedException("bad creds"), "sync-auth-failed" },
            { new SyncConflictException("remote changed"), "sync-conflict" },
            { new SyncNetworkException("timed out"), "sync-network" },
            { new SyncCorruptFileException("bad checksum"), "sync-corrupt-file" },
            { new AccessDeniedException("memory_delete requires mode full (current rw)"), "access-denied" },
            { new ValidationException("projectId is required"), "invalid-params" },
            { new JsonException("The JSON value could not be converted to System.String[]."), "invalid-argument" },
            { new ArgumentException("The arguments dictionary is missing a value for the required parameter 'content'."), "invalid-argument" },
            { new ArgumentNullException("projectIds"), "invalid-argument" },
            { new UnknownHashException("deadbeef", "acme"), "unknown-hash" },
            {
                new UnsupportedSchemaVersionException("bank schema v4 is newer than this binary supports (v3); update ai-raccoon"),
                "schema-version-unsupported"
            }
        };

    private void DeleteRoot() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public Task IngestFile_OutsideScope_ReturnsRefusal_WithoutAnSdkErrorLog() =>
        AssertRefusalOverRealServerAsync(_dataRoot, "memory_ingest_file",
            new Dictionary<string, object?> { ["projectId"] = "acme", ["path"] = "/etc/passwd" },
            "path-outside-scope");

    /// <summary>
    ///     D7: a bank stamped ahead of CurrentVersion (MemorySchema.cs) is refused by EnsureAsync on
    ///     open, not an unhandled crash. Written standalone, not via AssertRefusalOverRealServerAsync,
    ///     because a broken bank's background jobs also fail open and log their own expected errors.
    /// </summary>
    [Fact]
    public async Task ForwardSchemaVersion_ReturnsRefusal_OnTheToolCall()
    {
        var dataRoot = TestData.CreateTempRoot("tool-refusals-e2e-schema-version-unsupported");
        try
        {
            await SeedForwardSchemaVersionAsync(dataRoot, TestContext.Current.CancellationToken);

            var (port, host) = await LoopbackPort.BindWithRetryAsync(async candidate =>
            {
                var started = McpServerSetup.CreateServerHost(
                    new ServerConfig(candidate, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot)));
                await started.StartAsync(TestContext.Current.CancellationToken);
                return (candidate, started);
            });
            try
            {
                using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Name = "tool-refusals-test",
                        Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                        TransportMode = HttpTransportMode.StreamableHttp
                    },
                    httpClient,
                    NullLoggerFactory.Instance,
                    true);
                await using var client = await McpClient.CreateAsync(transport,
                    cancellationToken: TestContext.Current.CancellationToken);

                var result = await client.CallToolAsync("memory_write",
                    new Dictionary<string, object?> { ["projectId"] = "acme", ["content"] = "x" },
                    cancellationToken: TestContext.Current.CancellationToken);

                result.IsError.ShouldBe(true);
                var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
                text.ShouldStartWith("schema-version-unsupported:");
            }
            finally
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Theory]
    [MemberData(nameof(RealServerRefusalCases))]
    public async Task KnownRefusal_ReturnsRefusal_WithoutAnSdkErrorLog(string toolName,
        Dictionary<string, object?> arguments, string expectedPrefix, string? accessModeProjectId,
        string? accessMode)
    {
        var dataRoot = TestData.CreateTempRoot($"tool-refusals-e2e-{expectedPrefix}");
        try
        {
            if (accessModeProjectId is not null)
            {
                await SeedProjectAccessModeAsync(dataRoot, accessModeProjectId, accessMode!,
                    TestContext.Current.CancellationToken);
            }

            await AssertRefusalOverRealServerAsync(dataRoot, toolName, arguments, expectedPrefix);
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     Everything the server said while handling the call, at Warning and above, with exception
    ///     types — the only place a CI-only refusal failure can be diagnosed from.
    /// </summary>
    private static string ServerDiagnostics(FakeLoggerProvider logs)
    {
        var records = logs.Collector.GetSnapshot()
            .Where(r => r.Level >= LogLevel.Warning)
            .Select(r => $"  [{r.Level}] {r.Category}: {r.Message}"
                         + (r.Exception is null ? string.Empty : $"\n    -> {r.Exception.GetType().FullName}: {r.Exception.Message}"))
            .ToList();

        return records.Count == 0
            ? "The server logged nothing at Warning or above."
            : "Server log records at Warning and above:\n" + string.Join("\n", records);
    }

    private static async Task AssertRefusalOverRealServerAsync(string dataRoot, string toolName,
        Dictionary<string, object?> arguments, string expectedPrefix)
    {
        var fakeLogs = new FakeLoggerProvider();
        var (port, host) = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var started = McpServerSetup.CreateServerHost(
                new ServerConfig(candidate, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot)));
            started.Services.GetRequiredService<ILoggerFactory>().AddProvider(fakeLogs);
            await started.StartAsync(TestContext.Current.CancellationToken);
            return (candidate, started);
        });
        try
        {
            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "tool-refusals-test",
                    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                httpClient,
                NullLoggerFactory.Instance,
                true);
            await using var client = await McpClient.CreateAsync(transport,
                cancellationToken: TestContext.Current.CancellationToken);

            var result = await client.CallToolAsync(toolName, arguments,
                cancellationToken: TestContext.Current.CancellationToken);

            result.IsError.ShouldBe(true);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

            // When the prefix is missing the tool threw something ToolRefusals does not map, and the
            // SDK replaces it with "An error occurred invoking '<tool>'." — eleven words carrying
            // neither the type nor the message. Without the server's own log records in the failure,
            // a CI-only failure here cannot be diagnosed at all; that is what made this class's
            // intermittent reds unreadable (2026-08-15 project-scope review, WP19).
            text.ShouldStartWith($"{expectedPrefix}:", customMessage: ServerDiagnostics(fakeLogs));

            // Every fail-level log record here is a real crash, never a refusal.
            var errors = fakeLogs.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Error).ToList();
            errors.ShouldBeEmpty(string.Join('\n', errors.Select(e => e.Message)));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Writes the per-project access mode row directly (same DataRoot the server host reads per call — see McpServerFactory).</summary>
    private static async Task SeedProjectAccessModeAsync(string dataRoot, string projectId, string mode,
        CancellationToken cancellationToken)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        var store = TestData.CreateMemoryStore(factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(factory), TestData.RealMarkdownChunker(), TimeProvider.System,
            TestData.CreateEmbeddingService());
        await store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), mode, cancellationToken);
    }

    /// <summary>Opens the bank once (creating and migrating it to CurrentVersion), then stamps user_version one past it.</summary>
    private static async Task SeedForwardSchemaVersionAsync(string dataRoot, CancellationToken cancellationToken)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
                $"PRAGMA user_version = {MemorySchema.CurrentVersion + 1}", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }


    [Theory]
    [MemberData(nameof(MappedRefusals))]
    public void PrefixFor_MapsEachKnownRefusalType(Exception exception, string expectedPrefix) => ToolRefusals.PrefixFor(exception).ShouldBe(expectedPrefix);

    [Fact]
    public void PrefixFor_RejectsAnUnlistedType()
    {
        // The encryption family stays fail-level on purpose: a bad key source is a real fault, not a refusal.
        ToolRefusals.PrefixFor(new BankKeyMismatchException("bank open failed")).ShouldBeNull();

        // ArgumentOutOfRangeException is how .NET reports our own index arithmetic going wrong.
        // Mapping it would mute Error-level alerting and tell the caller to retry a blameless argument.
        ToolRefusals.PrefixFor(new ArgumentOutOfRangeException("limit")).ShouldBeNull();
    }

    [Fact]
    public async Task KnownRefusal_IsWarningWithoutExceptionDetails()
    {
        var dataRoot = TestData.CreateTempRoot("tool-refusals-warning");
        try
        {
            var fakeLogs = new FakeLoggerProvider();
            var (port, host) = await LoopbackPort.BindWithRetryAsync(async candidate =>
            {
                var started = McpServerSetup.CreateServerHost(
                    new ServerConfig(candidate, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot)));
                started.Services.GetRequiredService<ILoggerFactory>().AddProvider(fakeLogs);
                await started.StartAsync(TestContext.Current.CancellationToken);
                return (candidate, started);
            });
            try
            {
                using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
                var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Name = "tool-refusals-warning-test",
                        Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                        TransportMode = HttpTransportMode.StreamableHttp
                    },
                    httpClient,
                    NullLoggerFactory.Instance,
                    true);
                await using var client = await McpClient.CreateAsync(transport,
                    cancellationToken: TestContext.Current.CancellationToken);

                var result = await client.CallToolAsync("memory_share",
                    new Dictionary<string, object?>
                    {
                        ["projectId"] = "acme",
                        ["hash"] = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd"
                    },
                    cancellationToken: TestContext.Current.CancellationToken);

                result.IsError.ShouldBe(true);
                var record = fakeLogs.Collector.GetSnapshot()
                    .Single(r => r.Message.Contains("unknown-hash", StringComparison.Ordinal));
                record.Level.ShouldBe(LogLevel.Warning);
                record.Exception.ShouldBeNull();
                record.Message.ShouldNotContain("No entry with hash");
            }
            finally
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    [Theory]
    [InlineData("sync-network", LogLevel.Warning)]
    [InlineData("sync-corrupt-file", LogLevel.Warning)]
    [InlineData("unknown-hash", LogLevel.Warning)]
    [InlineData("path-outside-scope", LogLevel.Information)]
    [InlineData("access-denied", LogLevel.Information)]
    [InlineData("invalid-params", LogLevel.Information)]
    [InlineData("confirm-required", LogLevel.Information)]
    public void LevelFor_LogsInfrastructureFaultsAtWarning(string prefix, LogLevel expectedLevel) => ToolRefusals.LevelFor(prefix).ShouldBe(expectedLevel);

    /// <summary>
    ///     Doc/code drift guard, both directions: every prefix the reference doc's error-shapes table
    ///     promises must exist in code, and every code-known prefix must be documented — no
    ///     hand-duplicated expectation list on either side.
    /// </summary>
    [Fact]
    public void DocumentedPrefixes_MatchCodeExactlyInBothDirections()
    {
        var doc = File.ReadAllText(TestData.RepoFile("docs/reference/agent-memory-server.md"));
        var section = ErrorShapesSection(doc);
        var documentedPrefixes = Regex.Matches(section, @"^\| `([a-z][a-z-]*)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        documentedPrefixes.ShouldNotBeEmpty("the error-shapes table regex matched nothing — has the doc's format changed?");

        var codePrefixes = ToolRefusals.RefusalPrefixes.Values
            .Concat(ToolRefusals.DirectThrowPrefixes)
            .ToHashSet(StringComparer.Ordinal);

        var documentedButNotInCode = documentedPrefixes.Except(codePrefixes).ToList();
        var inCodeButNotDocumented = codePrefixes.Except(documentedPrefixes).ToList();

        documentedButNotInCode.ShouldBeEmpty(
            $"documented in agent-memory-server.md but not in ToolRefusals: {string.Join(", ", documentedButNotInCode)}");
        inCodeButNotDocumented.ShouldBeEmpty(
            $"known to ToolRefusals but missing from agent-memory-server.md's error-shapes table: {string.Join(", ", inCodeButNotDocumented)}");
    }

    private static string ErrorShapesSection(string doc)
    {
        const string heading = "## Error shapes";
        var start = doc.IndexOf(heading, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "could not find the '## Error shapes' heading in agent-memory-server.md");
        var end = doc.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? doc[start..] : doc[start..end];
    }
}
