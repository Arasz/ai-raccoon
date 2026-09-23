using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Observability;
using AiRaccoon.Tests.TestHelpers;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     One `ai-raccoon serve` of the built binary, on a given root and port, as its own process.
///     Start returns once /observability on that port names this very process; disposal stops it
///     through its own token-guarded /shutdown and kills it only if that fails.
/// </summary>
internal sealed class RealServe : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A cold serve on a loaded runner; the harness cap, not a verdict.</summary>
    private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _stdout = new();

    private RealServe(Process process, InfrastructureOptions options, int port)
    {
        Process = process;
        Options = options;
        Port = port;
    }

    public Process Process { get; }

    public InfrastructureOptions Options { get; }

    public int Port { get; }

    public Uri Endpoint => ServerProbe.EndpointFor(Port);

    public string Stderr
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    public string Stdout
    {
        get
        {
            lock (_stdout)
            {
                return _stdout.ToString();
            }
        }
    }

    /// <summary>Starts `serve --port <paramref name="port"/>` on <paramref name="options"/>' root and scope.</summary>
    public static Task<RealServe> StartAsync(InfrastructureOptions options, int port, CancellationToken cancellationToken,
        params string[] extraArguments) =>
        StartAsync(options, ["serve", "--port", port.ToString(CultureInfo.InvariantCulture), .. extraArguments], port,
            cancellationToken);

    /// <summary>Starts the binary with the root/scope flags followed by <paramref name="verbArguments"/>, and
    /// waits until <paramref name="port"/> answers as this process.</summary>
    public static async Task<RealServe> StartAsync(InfrastructureOptions options, string[] verbArguments, int port,
        CancellationToken cancellationToken)
    {
        Guard.IsNotNull(options);
        var startInfo = new ProcessStartInfo(AiRaccoonProcess.Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in (string[])
                 [
                     "--data-root", options.DataRoot,
                     "--install-scope", options.Scope.ToString().ToLowerInvariant(),
                     .. verbArguments
                 ])
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)!;
        var serve = new RealServe(process, options, port);
        process.OutputDataReceived += (_, line) => serve.Append(serve._stdout, line.Data);
        process.ErrorDataReceived += (_, line) => serve.Append(serve._stderr, line.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await serve.WaitUntilServingAsync(cancellationToken);
        }
        catch
        {
            await serve.DisposeAsync();
            throw;
        }

        return serve;
    }

    /// <summary>The pid /observability reports on <paramref name="port"/>, or null when nothing ai-raccoon answers.</summary>
    public static async Task<int?> PidOnAsync(int port, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var info = await http.GetFromJsonAsync<ServerInfo>(
                $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{ObservabilityEndpoint.Path}", JsonOptions,
                cancellationToken);
            return info is { Name: ServerInfo.ServerName } ? info.Pid : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
                                       && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Stops a server by its own token-guarded /shutdown, then kills its pid if it did not go.</summary>
    public static async Task StopByPortAsync(int port, InfrastructureOptions options)
    {
        if (await PidOnAsync(port, CancellationToken.None) is not { } pid)
        {
            return;
        }

        await RequestShutdownAsync(port, new McpTokenFile(options).Read());
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(ExitWait);
            }
            catch (TimeoutException)
            {
                await RaccoonProcess.KillTreeAndWaitAsync(process, ExitWait, CancellationToken.None);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!Process.HasExited)
        {
            await RequestShutdownAsync(Port, new McpTokenFile(Options).Read());
            try
            {
                await Process.WaitForExitAsync().WaitAsync(ExitWait);
            }
            catch (TimeoutException)
            {
                await RaccoonProcess.KillTreeAndWaitAsync(Process, ExitWait, CancellationToken.None);
            }
        }

        Process.Dispose();
    }

    private static async Task RequestShutdownAsync(int port, string? token)
    {
        if (token is null)
        {
            return;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{ShutdownEndpoint.Path}");
        request.Headers.Add(McpTokenGate.HeaderName, token);
        try
        {
            using var response = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Already gone: the wait that follows is the verdict.
        }
    }

    private void Append(StringBuilder sink, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sink)
        {
            sink.AppendLine(line);
        }
    }

    private async Task WaitUntilServingAsync(CancellationToken cancellationToken)
    {
        var served = await WaitByPolling.WaitForAsync(
            async () => Process.HasExited || await PidOnAsync(Port, cancellationToken) == Process.Id,
            WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, StartBudget, TimeProvider.System,
            cancellationToken);
        if (!served || Process.HasExited)
        {
            throw new InvalidOperationException(
                $"serve on port {Port} never answered as pid {Process.Id} (exited: {Process.HasExited}); stdout: {Stdout} stderr: {Stderr}");
        }
    }
}
