using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A listener that looks like an ai-raccoon server to the probe and to /observability, but
///     answers /shutdown however the test says and never actually stops.
/// </summary>
internal sealed class FakeRaccoon : IAsyncDisposable
{
    private const string JsonRpcRefusal = """{"jsonrpc":"2.0","id":null,"error":{"code":-32001,"message":"nope"}}""";

    private readonly WebApplication _app;
    private readonly FakeRaccoonProof? _proof;
    private readonly List<string> _proofNonces = [];
    private readonly List<string> _shutdownTokenHeaders = [];

    private string? _lastProofKeyId;
    private string? _lastProofSignature;

    private FakeRaccoon(WebApplication app, int port, FakeRaccoonProof? proof)
    {
        _app = app;
        Port = port;
        _proof = proof;
    }

    public int Port { get; }

    public int ShutdownRequests { get; private set; }

    /// <summary>GET /observability requests from anyone but the fake's own warm-up — the D5 measurement:
    /// an unproven listener may receive only the probe and the challenge.</summary>
    public int ObservabilityRequests => Volatile.Read(ref _observabilityRequests);

    private int _observabilityRequests;

    /// <summary>When true, every challenge is answered with the last proof this fake produced —
    /// a captured response replayed against a fresh challenge.</summary>
    public bool ReplayLastProof { get; set; }

    /// <summary>Every nonce /identity/prove received, in order — the freshness measurement.</summary>
    public IReadOnlyList<string> ProofNonces
    {
        get
        {
            lock (_proofNonces)
            {
                return [.. _proofNonces];
            }
        }
    }

    /// <summary>Every X-AiRaccoon-Token value /shutdown received — the F70 measurement: a listener
    /// that merely claims the name must never see the data root's token.</summary>
    public IReadOnlyList<string> ShutdownTokenHeaders
    {
        get
        {
            lock (_shutdownTokenHeaders)
            {
                return [.. _shutdownTokenHeaders];
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>Starts on <paramref name="port"/>, answering /shutdown with <paramref name="shutdownStatus"/>.
    /// <paramref name="name"/> is what /observability claims to be; a null <paramref name="version"/>
    /// leaves the field out entirely, like a server predating ADR-0022. A non-null <paramref name="proof"/>
    /// maps the ADR-0106 D2 route, honestly or mis-programmed as the record says.</summary>
    public static async Task<FakeRaccoon> StartAsync(int port, HttpStatusCode shutdownStatus,
        CancellationToken cancellationToken, string name = "ai-raccoon", string? version = "0.0.0-fake",
        FakeRaccoonProof? proof = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        var fake = new FakeRaccoon(app, port, proof);

        var otlp = new { enabled = false, endpoint = (string?)null, protocol = (string?)null };
        app.MapPost("/mcp", () => Results.Text(JsonRpcRefusal, "application/json", null, StatusCodes.Status401Unauthorized));
        app.MapGet("/observability", () =>
        {
            Interlocked.Increment(ref fake._observabilityRequests);
            return version is null
                ? Results.Json(new { name, pid = Environment.ProcessId, otlp })
                : Results.Json(new { name, version, pid = Environment.ProcessId, otlp });
        });
        app.MapPost("/shutdown", (HttpContext context) =>
        {
            var token = context.Request.Headers[McpTokenGate.HeaderName].ToString();
            if (token.Length > 0)
            {
                lock (fake._shutdownTokenHeaders)
                {
                    fake._shutdownTokenHeaders.Add(token);
                }
            }

            fake.ShutdownRequests++;
            return Results.StatusCode((int)shutdownStatus);
        });
        if (proof is not null)
        {
            app.MapPost(IdentityProof.EndpointPath, fake.ProofAsync);
        }

        await app.StartAsync(cancellationToken);

        // Kestrel's StartAsync returns once the socket is bound, not once it can serve. The first
        // request still pays pipeline warm-up, and ServerProbe gives a listener ~1s to answer — on
        // a loaded runner that warm-up has exceeded the budget, so the probe recorded "no answer"
        // and the test under it saw the unanswered path instead of the one it was written for.
        // Answering our own /observability once removes that race for every caller.
        await WaitUntilAnsweringAsync(port, cancellationToken);
        Interlocked.Exchange(ref fake._observabilityRequests, 0);
        return fake;
    }

    private static async Task WaitUntilAnsweringAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        while (true)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/observability", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           && !cancellationToken.IsCancellationRequested)
            {
                // Not serving yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    /// <summary>Answers the frozen D2 challenge, or the mis-programmed answer the record asks for.</summary>
    private async Task ProofAsync(HttpContext context)
    {
        var proof = _proof!;
        if (proof.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(proof.Delay, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // The prover gave up first; nothing to answer.
                return;
            }
        }

        context.Response.ContentType = "application/json";
        if (proof.RawResponse is { } raw)
        {
            await context.Response.WriteAsync(raw, context.RequestAborted);
            return;
        }

        var body = await ReadBodyAsync(context.Request, context.RequestAborted);
        if (proof.RelayTo is { } target)
        {
            await RelayAsync(context, target, body);
            return;
        }

        var nonce = NonceOf(body);
        lock (_proofNonces)
        {
            _proofNonces.Add(nonce);
        }

        if (ReplayLastProof && _lastProofSignature is { } replayed)
        {
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { v = 1, keyId = _lastProofKeyId, signature = replayed }),
                context.RequestAborted);
            return;
        }

        var keyId = IdentityProof.KeyId(proof.Signer);
        var port = proof.PortOverride ?? context.Connection.LocalPort;
        _lastProofKeyId = keyId;
        _lastProofSignature = proof.DerSignature
            ? Base64Url.EncodeToString(proof.Signer.SignHash(
                SHA256.HashData(IdentityProof.Transcript(nonce, keyId, proof.RootFp, port)),
                DSASignatureFormat.Rfc3279DerSequence))
            : IdentityProof.Sign(proof.Signer, nonce, keyId, proof.RootFp, port);
        await context.Response.WriteAsync(
            JsonSerializer.Serialize(new { v = 1, keyId, signature = _lastProofSignature }),
            context.RequestAborted);
    }

    /// <summary>Forwards the challenge verbatim and returns the downstream answer — a real relay.</summary>
    private static async Task RelayAsync(HttpContext context, Uri target, byte[] body)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var forward = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = new ByteArrayContent(body)
        };
        forward.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(forward, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(
            await response.Content.ReadAsStringAsync(context.RequestAborted), context.RequestAborted);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static string NonceOf(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("nonce", out var nonce)
                ? nonce.GetString() ?? ""
                : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}

/// <summary>
///     How the fake answers /identity/prove. The default is the honest signing path; every other
///     member mis-programs one half of the wire so an adversarial gate has a listener to talk to.
/// </summary>
internal sealed record FakeRaccoonProof
{
    /// <summary>The key the fake signs with — normally the state-dir key the test minted.</summary>
    public required ECDsa Signer { get; init; }

    /// <summary>The root fingerprint the fake claims to serve.</summary>
    public required string RootFp { get; init; }

    /// <summary>Sign the transcript with this port instead of the bound one (a relayed backend's port).</summary>
    public int? PortOverride { get; init; }

    /// <summary>Sign DER instead of the frozen IEEE-P1363 fixed-field concatenation.</summary>
    public bool DerSignature { get; init; }

    /// <summary>Return this raw body for every challenge — garbage, oversized and canned replay fixtures.</summary>
    public string? RawResponse { get; init; }

    /// <summary>Delay the answer by this long — a slow or hanging proof channel.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Forward the challenge verbatim to this target and return its answer (a real relay).</summary>
    public Uri? RelayTo { get; init; }
}
