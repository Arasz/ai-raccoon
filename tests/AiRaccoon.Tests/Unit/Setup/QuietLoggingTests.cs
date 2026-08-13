using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Logging;
using AiRaccoon.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Pins the owner's quiet-mode ruling (2026-08-09): every level reaches a file, nothing
///     reaches stdout/stderr, on every transport; an unwritable log path degrades to silence
///     rather than crashing; non-quiet console behaviour is unchanged. Console.Out/Error are
///     process-global, so this class disables parallelization with itself
///     (QuietLoggingCollection) — it cannot rule out interference from unrelated tests that
///     write to Console concurrently in a different collection.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(QuietLoggingCollection.Name)]
public sealed class QuietLoggingTests : IDisposable
{
    private readonly List<string> _dataRoots = [];

    public void Dispose()
    {
        foreach (var root in _dataRoots.Where(Directory.Exists))
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Quiet_Stdio_NothingReachesStdoutOrStderr_ButInformationReachesTheFile()
    {
        var options = QuietOptions(InstallScope.User);
        var config = new ServerConfig(DefaultOptions.Port, McpTransport.Stdio, options);

        var (stdout, stderr) = CaptureConsole(() =>
        {
            using var host = McpServerSetup.CreateServerHost(config);
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("quiet-test");
            logger.LogInformation("quiet-stdio-info-marker");
            logger.LogWarning("quiet-stdio-warn-marker");
        });

        stderr.ShouldBeEmpty();
        stdout.ShouldBeEmpty();
        File.ReadAllText(LogFilePath(options)).ShouldContain("quiet-stdio-info-marker");
    }

    /// <summary>D6: combined mode (stdio + http together) shares CreateWebHost with plain HTTP,
    /// so the contract must hold there too — this is the site the second gate's ruling added.</summary>
    [Fact]
    public void Quiet_Combined_NothingReachesStdoutOrStderr_ButInformationReachesTheFile()
    {
        var options = QuietOptions(InstallScope.Project);
        var config = new ServerConfig(0, McpTransport.Http, options);

        var (stdout, stderr) = CaptureConsole(() =>
        {
            using var host = McpServerSetup.CreateServerHost(config, [McpTransport.Stdio, McpTransport.Http], TimeProvider.System);
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("quiet-test");
            logger.LogInformation("quiet-combined-info-marker");
            logger.LogWarning("quiet-combined-warn-marker");
        });

        stderr.ShouldBeEmpty();
        stdout.ShouldBeEmpty();
        File.ReadAllText(LogFilePath(options)).ShouldContain("quiet-combined-info-marker");
    }

    /// <summary>Same principle as WP4's endpoint guard: an observability misconfiguration must
    /// never take the server down. A directory occupying the log file's path makes it unwritable.</summary>
    [Fact]
    public async Task Quiet_UnwritableLogPath_HostStillStartsAndServes()
    {
        var options = QuietOptions(InstallScope.Project);
        Directory.CreateDirectory(LogFilePath(options));

        var config = new ServerConfig(0, McpTransport.Http, options);
        using var host = McpServerSetup.CreateServerHost(config);
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var tools = ActivatorUtilities.CreateInstance<MemoryTools>(host.Services);
            var envelope = await tools.Stats("quiet-unwritable-probe", TestContext.Current.CancellationToken);

            envelope.Data.ShouldNotBeNull();
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void NonQuietHost_KeepsInformation_OnConsole()
    {
        var config = new ServerConfig(DefaultOptions.Port, McpTransport.Stdio, LoudOptions(InstallScope.User));

        var entries = Capture(logger => logger.LogInformation("loud-info-marker"), config);

        entries.ShouldContain(e => e.Contains("loud-info-marker", StringComparison.Ordinal));
    }

    /// <summary>Per-request ASP.NET Core chatter and the MCP request-handler INFO lines flood a
    /// non-quiet serve log; this pins the default-level fix without reaching for --quiet, which
    /// would also silence AiRaccoon's own Information logs.</summary>
    [Fact]
    public void HttpHost_QuietsAspNetCoreAndMcpServerCategories_ButKeepsAppInfo()
    {
        var config = new ServerConfig(0, McpTransport.Http, LoudOptions(InstallScope.User));

        var entries = CaptureFactory(factory =>
        {
            var aspNetCore = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
            aspNetCore.LogInformation("aspnet-info-marker");
            aspNetCore.LogWarning("aspnet-warn-marker");

            var routing = factory.CreateLogger("Microsoft.AspNetCore.Routing.EndpointMiddleware");
            routing.LogInformation("routing-info-marker");

            var mcpServer = factory.CreateLogger("ModelContextProtocol.Server.McpServer");
            mcpServer.LogInformation("mcp-info-marker");

            var app = factory.CreateLogger("AiRaccoon.Something");
            app.LogInformation("app-info-marker");
        }, config);

        entries.ShouldNotContain(e => e.Contains("aspnet-info-marker", StringComparison.Ordinal));
        entries.ShouldContain(e => e.Contains("aspnet-warn-marker", StringComparison.Ordinal));
        entries.ShouldNotContain(e => e.Contains("routing-info-marker", StringComparison.Ordinal));
        entries.ShouldNotContain(e => e.Contains("mcp-info-marker", StringComparison.Ordinal));
        entries.ShouldContain(e => e.Contains("app-info-marker", StringComparison.Ordinal));
    }

    /// <summary>J1: HostLogging.Configure's quiet branch returned before the transport floors were
    /// applied, so a quiet HTTP serve wrote every ASP.NET Core/MCP-server INFO line to the file —
    /// destination is chosen by quiet, level policy by transport, and both must hold together.</summary>
    [Fact]
    public void QuietHttp_QuietsAspNetCoreCategory_ButKeepsAppInfo_InTheFile()
    {
        var options = QuietOptions(InstallScope.Project);
        var config = new ServerConfig(0, McpTransport.Http, options);

        var (stdout, stderr) = CaptureConsole(() =>
        {
            using var host = McpServerSetup.CreateServerHost(config);
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("quiet-http-aspnet-info-marker");
            factory.CreateLogger("AiRaccoon.Something").LogInformation("quiet-http-app-info-marker");
        });

        stdout.ShouldBeEmpty();
        stderr.ShouldBeEmpty();
        var fileContent = File.ReadAllText(LogFilePath(options));
        fileContent.ShouldNotContain("quiet-http-aspnet-info-marker");
        fileContent.ShouldContain("quiet-http-app-info-marker");
    }

    private InfrastructureOptions QuietOptions(InstallScope scope) => Options(scope, quiet: true);

    private InfrastructureOptions LoudOptions(InstallScope scope) => Options(scope, quiet: false);

    private InfrastructureOptions Options(InstallScope scope, bool quiet)
    {
        var root = TestData.CreateTempRoot("ai-raccoon-quiet-logging");
        _dataRoots.Add(root);
        return new InfrastructureOptions { DataRoot = root, Scope = scope, Quiet = quiet };
    }

    private static string LogFilePath(InfrastructureOptions options) => QuietLogging.LogFilePath(options);

    private static (string Stdout, string Stderr) CaptureConsole(Action action)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private static List<string> Capture(Action<ILogger> emit, ServerConfig config)
    {
        using var host = McpServerSetup.CreateServerHost(config);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var recorder = new RecorderProvider();
        factory.AddProvider(recorder);
        emit(factory.CreateLogger("quiet-test"));
        return recorder.Entries;
    }

    private static List<string> CaptureFactory(Action<ILoggerFactory> emit, ServerConfig config)
    {
        using var host = McpServerSetup.CreateServerHost(config);
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var recorder = new RecorderProvider();
        factory.AddProvider(recorder);
        emit(factory);
        return recorder.Entries;
    }

    private sealed class RecorderProvider : ILoggerProvider
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecorderLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecorderLogger(List<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Add($"{logLevel}: {formatter(state, exception)}");
        }
    }
}
