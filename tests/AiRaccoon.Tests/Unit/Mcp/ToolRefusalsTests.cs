using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Sync;
using AiRaccoon.Core.Watch;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Serve;
using AiRaccoon.Tools;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     #151: a correct refusal (path-outside-scope) must not read as a crash — the SDK logs Error
///     on every exception escaping a tool, McpException included, so the old catch-and-rethrow
///     shape produced an Error record for an expected refusal. Drives a real McpServer over the
///     real HTTP pipeline so the assertion exercises the SDK's own exception handling.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolRefusalsTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("tool-refusals-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public Task IngestFile_OutsideScope_ReturnsRefusal_WithoutAnSdkErrorLog() =>
        AssertRefusalOverRealServerAsync(_dataRoot, "memory_ingest_file",
            new Dictionary<string, object?> { ["projectId"] = "acme", ["path"] = "/etc/passwd" },
            "path-outside-scope");

    /// <summary>
    ///     #151/e2e-prefix-coverage: only path-outside-scope was ever proven through a real
    ///     McpServer; access-denied (project set read-only, then a write tool) and
    ///     unknown-workspace (memory_write against a workspaceId that doesn't exist) now are too.
    ///     Sync prefixes are skipped here — they need real cloud config, not just a local bank.
    /// </summary>
    public static TheoryData<string, Dictionary<string, object?>, string, string?> RealServerRefusalCases => new()
    {
        {
            "memory_write",
            new Dictionary<string, object?> { ["projectId"] = "acme-ro", ["content"] = "x" },
            "access-denied",
            "acme-ro"
        },
        {
            "memory_write",
            new Dictionary<string, object?> { ["projectId"] = "acme", ["content"] = "x", ["workspaceId"] = "ws-bogus" },
            "unknown-workspace",
            null
        },
        {
            // D1: 'keep' is documented as accepting the scalar "all", but the schema declares
            // string[] — the SDK's argument marshaller throws a raw JsonException instead of a
            // typed refusal (see docs/reference/agent-memory-server.md Error shapes).
            "memory_workspace_consolidate",
            new Dictionary<string, object?> { ["projectId"] = "acme", ["workspaceId"] = "ws-1", ["keep"] = "all" },
            "invalid-argument",
            null
        }
    };

    [Theory]
    [MemberData(nameof(RealServerRefusalCases))]
    public async Task KnownRefusal_ReturnsRefusal_WithoutAnSdkErrorLog(string toolName,
        Dictionary<string, object?> arguments, string expectedPrefix, string? readOnlyProjectId)
    {
        var dataRoot = TestData.CreateTempRoot($"tool-refusals-e2e-{expectedPrefix}");
        try
        {
            if (readOnlyProjectId is not null)
            {
                await SeedProjectAccessModeAsync(dataRoot, readOnlyProjectId, "ro",
                    TestContext.Current.CancellationToken);
            }

            await AssertRefusalOverRealServerAsync(dataRoot, toolName, arguments, expectedPrefix);
        }
        finally
        {
            Directory.Delete(dataRoot, true);
        }
    }

    private static async Task AssertRefusalOverRealServerAsync(string dataRoot, string toolName,
        Dictionary<string, object?> arguments, string expectedPrefix)
    {
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(
            new ServerConfig(port, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot), default));
        var fakeLogs = new FakeLoggerProvider();
        host.Services.GetRequiredService<ILoggerFactory>().AddProvider(fakeLogs);

        await host.StartAsync(TestContext.Current.CancellationToken);
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
            text.ShouldStartWith($"{expectedPrefix}:");

            // The defect #151 names: every fail-level record here is a real crash, none are refusals.
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
        var store = new SqliteMemoryStore(
            new SqliteConnectionFactory(options,
                new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                    [new EnvEncryptionKeyProvider()])),
            TimeProvider.System, new TokenizerChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        await store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), mode, cancellationToken);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static TheoryData<Exception, string> MappedRefusals => new()
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
        { new JsonException("The JSON value could not be converted to System.String[]."), "invalid-argument" }
    };

    [Theory]
    [MemberData(nameof(MappedRefusals))]
    public void PrefixFor_MapsEachKnownRefusalType(Exception exception, string expectedPrefix) =>
        ToolRefusals.PrefixFor(exception).ShouldBe(expectedPrefix);

    [Fact]
    public void PrefixFor_RejectsAnUnlistedType()
    {
        // The encryption family stays fail-level on purpose: a bad key source is a real fault, not a refusal.
        ToolRefusals.PrefixFor(new BankKeyMismatchException("bank open failed")).ShouldBeNull();
    }

    [Theory]
    [InlineData("sync-network", LogLevel.Warning)]
    [InlineData("sync-corrupt-file", LogLevel.Warning)]
    [InlineData("path-outside-scope", LogLevel.Information)]
    [InlineData("access-denied", LogLevel.Information)]
    [InlineData("invalid-params", LogLevel.Information)]
    [InlineData("confirm-required", LogLevel.Information)]
    public void LevelFor_LogsInfrastructureFaultsAtWarning(string prefix, LogLevel expectedLevel) =>
        ToolRefusals.LevelFor(prefix).ShouldBe(expectedLevel);

    /// <summary>
    ///     Doc/code drift guard, both directions: every prefix the reference doc's error-shapes
    ///     table row promises must exist in code, and every code-known prefix (mapped exception
    ///     types plus the prefixes thrown directly as a bare <see cref="McpException" />) must be
    ///     documented — no hand-duplicated expectation list on either side.
    /// </summary>
    [Fact]
    public void DocumentedPrefixes_MatchCodeExactlyInBothDirections()
    {
        var doc = File.ReadAllText(RepoFile("docs/reference/agent-memory-server.md"));
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

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not locate {relative} from the test output directory.");
    }
}
