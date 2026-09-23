using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AiRaccoon.Tests.TestHelpers;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     The built proxy driven over raw JSON-RPC on its stdio, the way a well-behaved MCP client ends
///     a session: close stdin and wait. The SDK's stdio client waits out its shutdown timeout before
///     it closes stdin and then kills the tree, so the proxy's own shutdown — proving and stopping its
///     private backends — never runs under it; this driver lets it run.
/// </summary>
internal sealed class ProxyProcess : IAsyncDisposable
{
    /// <summary>The stateless revision: per-request metadata, no initialize, no session to close.</summary>
    public const string Stateless = "2026-07-28";

    /// <summary>The newest stateful revision: initialize opens a session the backend side must later close.</summary>
    public const string Stateful = "2025-11-25";

    private static readonly TimeSpan ReplyBudget = TimeSpan.FromSeconds(90);

    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly StringBuilder _stderr = new();
    private readonly Process _process;
    private readonly string _revision;
    private bool _initialized;
    private int _nextId;

    private ProxyProcess(Process process, string revision)
    {
        _process = process;
        _revision = revision;
    }

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

    public bool HasExited => _process.HasExited;

    /// <summary>Starts the binary with <paramref name="arguments"/> and pumps its stdout and stderr; the
    /// client speaks <paramref name="revision"/> (<see cref="Stateless"/> or <see cref="Stateful"/>).</summary>
    public static ProxyProcess Start(IEnumerable<string> arguments, string revision)
    {
        Guard.IsNotNull(arguments);
        var startInfo = new ProcessStartInfo(AiRaccoonProcess.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var proxy = new ProxyProcess(Process.Start(startInfo)!, revision);
        proxy._process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null)
            {
                proxy._stdout.Writer.TryComplete();
            }
            else
            {
                proxy._stdout.Writer.TryWrite(line.Data);
            }
        };
        proxy._process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null)
            {
                lock (proxy._stderr)
                {
                    proxy._stderr.AppendLine(line.Data);
                }
            }
        };
        proxy._process.BeginOutputReadLine();
        proxy._process.BeginErrorReadLine();
        return proxy;
    }

    /// <summary>tools/list (after initialize on a stateful revision); returns the tool names.</summary>
    public async Task<string[]> ListToolsAsync(CancellationToken cancellationToken)
    {
        await InitializeOnceAsync(cancellationToken);
        var tools = await RequestAsync("tools/list", new JsonObject(), cancellationToken);
        return [.. tools.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)];
    }

    /// <summary>tools/call; returns the text of the result's first content block, throwing on a tool error.</summary>
    public async Task<string> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        await InitializeOnceAsync(cancellationToken);
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments },
            cancellationToken);
        var text = result.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            throw new InvalidOperationException($"{name} failed: {text}");
        }

        return text;
    }

    /// <summary>Closes stdin — the end of the session — and waits for the proxy's own shutdown to finish.</summary>
    public async Task<int> CloseAsync(TimeSpan bound)
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Already gone.
            }
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(bound);
        }
        catch (TimeoutException)
        {
            RaccoonProcess.KillTree(_process);
            throw new TimeoutException($"the proxy did not exit within {bound} of stdin closing; stderr: {Stderr}");
        }

        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            await RaccoonProcess.KillTreeAndWaitAsync(_process, TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        _process.Dispose();
    }

    private async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        if (_revision == Stateless)
        {
            parameters["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = Stateless,
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "proxy-process", ["version"] = "1" }
            };
        }

        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(ReplyBudget);
        try
        {
            while (true)
            {
                var line = await _stdout.Reader.ReadAsync(bound.Token);
                using var message = JsonDocument.Parse(line);
                if (!message.RootElement.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number
                                                                               || replyId.GetInt32() != id)
                {
                    continue;
                }

                if (message.RootElement.TryGetProperty("error", out var error))
                {
                    throw new InvalidOperationException($"{method} failed: {error}");
                }

                return message.RootElement.GetProperty("result").Clone();
            }
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"the proxy gave no reply to {method} (exited: {_process.HasExited}{(_process.HasExited ? $", code {_process.ExitCode}" : "")}); stderr: {Stderr}",
                ex);
        }
    }

    private async Task InitializeOnceAsync(CancellationToken cancellationToken)
    {
        if (_revision == Stateless || _initialized)
        {
            return;
        }

        _initialized = true;
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = _revision,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "proxy-process", ["version"] = "1" }
        }, cancellationToken);
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
    }

    private async Task SendAsync(JsonObject message)
    {
        await _process.StandardInput.WriteLineAsync(message.ToJsonString());
        await _process.StandardInput.FlushAsync();
    }
}
