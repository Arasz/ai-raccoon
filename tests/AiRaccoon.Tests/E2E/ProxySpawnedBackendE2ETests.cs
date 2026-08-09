using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Observability;
using AiRaccoon.Setup.Serve;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     The composition users actually get (ADR-0020): the proxy starts `serve` itself, and that
///     `serve` mints the loopback token and gates /mcp. A pre-started ungated backend cannot show
///     this — it is the one path where the gate is really in the way.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxySpawnedBackendE2ETests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _dataRoot = TestData.CreateTempRoot("proxy-spawned-backend");
    private int _port;

    public ValueTask InitializeAsync()
    {
        _port = AiRaccoonProcess.FreePort();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     The proxy never kills the daemon it started, so the test does — via the PID
    ///     /observability reports, which stays open by design.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopSpawnedBackendAsync();
        try
        {
            Directory.Delete(_dataRoot, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }

    [Fact]
    public async Task ProxyOverASpawnedServe_CallsAToolThroughTheGate()
    {
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);

        var result = await client.CallToolAsync("memory_stats",
            new Dictionary<string, object?> { ["projectId"] = "acme" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldNotBe(true);
        // The backend really is a gated `serve`: it minted this file strictly before it bound.
        File.Exists(Path.Combine(_dataRoot, McpTokenFile.FileName)).ShouldBeTrue();
    }

    private async Task StopSpawnedBackendAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var info = await http.GetFromJsonAsync<ServerInfo>(
                $"http://127.0.0.1:{_port}/observability", JsonOptions, CancellationToken.None);
            if (info is { Name: "ai-raccoon" })
            {
                Process.GetProcessById(info.Pid).Kill(true);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                       or JsonException or ArgumentException or InvalidOperationException)
        {
            // Nothing listening, or the daemon already retired: nothing left to stop.
        }
    }
}
