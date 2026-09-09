using System.Runtime.InteropServices;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Watch;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Setup.Cli.Render;
using AiRaccoon.Setup.Logging;

namespace AiRaccoon;

public sealed partial class AppRunner
{
    private readonly CancellationTokenSource _cts = new();
    private readonly StandardStreams _streams = new(Console.In, Console.Out, Console.Error);
    private readonly Func<ServerConfig, ILoggerFactory, CancellationToken, Task<ISettingsStore>> _acquireServerSettingsStore;
    private readonly string? _processPath;

    public AppRunner() : this(CliSettingsBackend.AcquireAsync)
    {
    }

    /// <summary>Test seam: substitutes the settings-server acquisition (ADR-0075 §5.1) with a fake, so a
    /// command routed through the server never needs a real backend to answer.</summary>
    internal AppRunner(Func<ServerConfig, ILoggerFactory, CancellationToken, Task<ISettingsStore>> acquireServerSettingsStore)
        : this(acquireServerSettingsStore, Environment.ProcessPath)
    {
    }

    /// <summary>Test seam: also fixes this process's path, so the proxy's auto-start verdict does not
    /// depend on how the test host itself was launched (the dotnet muxer cannot be a backend).</summary>
    internal AppRunner(Func<ServerConfig, ILoggerFactory, CancellationToken, Task<ISettingsStore>> acquireServerSettingsStore, string? processPath)
    {
        _acquireServerSettingsStore = acquireServerSettingsStore;
        _processPath = processPath;
    }

    internal CancellationToken Token => _cts.Token;

    /// <summary>Test seam: counts how many times a one-shot path wired shutdown-signal cancellation —
    /// the observable that tells a routed launch (proxy or CLI command: 1) from one that returned
    /// early (parse failure, help/version: 0).</summary>
    internal int ShutdownCancellationRegistrations { get; private set; }

    public async Task<int> Run(string[] args)
    {
        if (GetCliInput(args) is not { } cliInput)
        {
            return ExitCode.FailedToParseCliArgs;
        }

        if (cliInput.ShowHelp || cliInput.ShowVersion)
        {
            return ExitCode.Success;
        }

        if (cliInput.IsCommandInput)
        {
            return await RunCliCommand(cliInput);
        }

        // A parse error on a path that would otherwise LAUNCH is fatal (docs/adr/0060).
        // CliArgs.TryParse reports success whenever the *option* read succeeded, so without this an
        // unrecognised verb printed "Unrecognized command or argument" and then launched the proxy
        // anyway — against the DEFAULT port and token file, not the --data-root the caller passed.
        // Deliberately after the verb branch: a known verb whose own argument is wrong keeps
        // ExitCode.InvalidArgument (15), which is how a script tells "you mistyped" from
        // "unparseable". GetCliInput has already rendered the message.
        if (cliInput.Errors.Count > 0)
        {
            return ExitCode.FailedToParseCliArgs;
        }

        // Bare launches proxy to the HTTP backend (ADR-0020, D2b): there is no in-process
        // server path anymore — the stdio plain host and the bare-http full server are deleted.
        return await RunProxy(cliInput);
    }

    private CliInput? GetCliInput(string[] args)
    {
        if (!CliArgs.TryParse(args, out var cliInput))
        {
            return null;
        }

        cliInput?.RenderTo(_streams);
        return cliInput;
    }

    /// <summary>
    ///     Wires SIGINT/SIGTERM to Token cancellation for a one-shot path (.NET-F3) — the CLI-command
    ///     and proxy paths get no host shutdown pipeline, so without this a signal just kills the
    ///     process outright instead of letting Token cancellation drive a graceful unwind.
    /// </summary>
    private IDisposable RegisterShutdownCancellation()
    {
        ShutdownCancellationRegistrations++;
        return new ShutdownSignalRegistration(
            PosixSignalRegistration.Create(PosixSignal.SIGINT, OnPosixSignal),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnPosixSignal));
    }

    /// <summary>Cancels Token instead of letting the runtime terminate the process outright.</summary>
    internal void OnPosixSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        _cts.Cancel();
    }

    private sealed class ShutdownSignalRegistration(PosixSignalRegistration first, PosixSignalRegistration second) : IDisposable
    {
        public void Dispose()
        {
            first.Dispose();
            second.Dispose();
        }
    }

    private async Task<int> RunCliCommand(CliInput cliInput)
    {
        using var shutdown = RegisterShutdownCancellation();
        var services = new ServiceCollection();
        ConfigureConsoleLogging(services, cliInput.ServerConfig.Options);
        services.RegisterCoreMemoryServices(cliInput.ServerConfig.Options);
        services.RegisterCommands();

        // Write-exclusivity (ADR-0075 §5.3): every command except the declared opt-out list is
        // bound to the server-backed store instead of the direct one RegisterCoreMemoryServices
        // just registered — this overrides that registration, the same pattern
        // SettingsStoreInjectionTests uses. The opt-out list never resolves this, so it never
        // pays for a probe or an auto-start. loggerFactory's lifetime must outlast the dispatch
        // below — LazyServerSettingsStore may not call the acquire closure until then.
        ILoggerFactory? settingsLoggerFactory = null;
        if (!CliWriteOptOuts.WritesDirectly(cliInput.CommandPath))
        {
            settingsLoggerFactory = CreateCliLoggerFactory(cliInput.ServerConfig.Options);
            var loggerFactory = settingsLoggerFactory;
            var lazyServerStore = new LazyServerSettingsStore(ctx => _acquireServerSettingsStore(cliInput.ServerConfig, loggerFactory, ctx));
            services.AddSingleton<ISettingsStore>(lazyServerStore);
            // ADR-0076: model embedding set routes the same way now — same instance, same acquired connection.
            services.AddSingleton<IModelMigrationStore>(lazyServerStore);
            // §3.3 D-E9: model code set local routes the same way too.
            services.AddSingleton<ICodeEngineStore>(lazyServerStore);
            // ADR-0075 amendment: repair routes the same way — same instance, same acquired connection.
            services.AddSingleton<IRepairStore>(lazyServerStore);
            // ADR-0075 amendment: extract prune and settings maintenance list route the same way too.
            services.AddSingleton<IPromotionQueuePruneStore>(lazyServerStore);
            services.AddSingleton<IMaintenanceStatsStore>(lazyServerStore);
            // ADR-0075 amendment: noise entries and watch registered route the same way too.
            services.AddSingleton<INoiseSummaryStore>(lazyServerStore);
            services.AddSingleton<IWatchRegisteredStore>(lazyServerStore);
        }

        try
        {
            await using var providder = services.BuildServiceProvider();
            var configCommands = providder.GetRequiredService<ConfigCommands>();
            return await configCommands.RunAsync(cliInput, _streams, Token);
        }
        finally
        {
            settingsLoggerFactory?.Dispose();
        }
    }

    private async Task<int> RunProxy(CliInput cliInput)
    {
        using var shutdown = RegisterShutdownCancellation();
        var services = new ServiceCollection();
        ConfigureConsoleLogging(services, cliInput.ServerConfig.Options);
        services.RegisterCoreMemoryServices(cliInput.ServerConfig.Options);
        services.RegisterProxyServices();
        await using var providder = services.BuildServiceProvider();
        var proxyRunner = providder.GetRequiredService<IProxyRunner>();
        return await proxyRunner.RunAsync(cliInput.ServerConfig, _streams, _processPath, Token);
    }

    /// <summary>One-shot graphs get no host logging pipeline; quiet mode must still reach them
    /// or a quiet stdio bridge prints per-request HttpClient lines to the terminal (QA-4).</summary>
    private static void ConfigureConsoleLogging(IServiceCollection services, InfrastructureOptions options) => services.AddLogging(builder => ConfigureCliLogging(builder, options));

    /// <summary>Standalone twin of <see cref="ConfigureConsoleLogging" />: the settings-server acquire
    /// (ADR-0075 §5.1) needs an <see cref="ILoggerFactory" /> before the CLI-command DI graph exists.</summary>
    private static ILoggerFactory CreateCliLoggerFactory(InfrastructureOptions options) => LoggerFactory.Create(builder => ConfigureCliLogging(builder, options));

    private static void ConfigureCliLogging(ILoggingBuilder builder, InfrastructureOptions options)
    {
        if (options.Quiet)
        {
            QuietLogging.Configure(builder, options);
        }
        else
        {
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        }
    }
}
