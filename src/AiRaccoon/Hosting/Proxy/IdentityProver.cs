using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     Proves a listener holds this root's identity key before any secret is handed over
///     (ADR-0106 D2). Null means proven; a value names why not. A root with no key file is
///     <see cref="IdentityProofFailure.NoKey" /> and the verifier creates zero files — minting
///     belongs to <c>serve</c> alone.
/// </summary>
public interface IIdentityProver
{
    /// <summary>
    ///     Challenges <paramref name="endpoint" />'s listener with a fresh nonce and verifies the
    ///     frozen transcript under this root's key. Null is proven; anything else is not.
    /// </summary>
    Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx);
}

/// <summary>
///     The client half of the proof channel: one CSPRNG nonce per request attempt (retries
///     included), the transcript reconstructed with the port actually dialled, IEEE-P1363
///     verification under the state-directory key, and a bounded time budget. Read-only — it never
///     mints and creates no files.
/// </summary>
public sealed class IdentityProver : IIdentityProver
{
    /// <summary>Long enough for a loaded loopback host, short enough to keep a fallback prompt.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(2);

    /// <summary>An answer larger than this is malformed, whatever it claims to be.</summary>
    public const int ResponseByteBound = 8 * 1024;

    private readonly TimeSpan _budget;
    private readonly Func<HttpClient> _client;
    private readonly IdentityKeyFile _keyFile;
    private readonly string _rootFp;

    /// <summary>A prover over one named client from the shared factory; the budget bounds one attempt.</summary>
    public IdentityProver(InfrastructureOptions options, IHttpClientFactory httpClientFactory, TimeSpan? budget = null)
        : this(options, () => httpClientFactory.CreateClient(nameof(IdentityProver)), budget)
    {
        Guard.IsNotNull(httpClientFactory);
    }

    /// <summary>A prover over one pre-configured client (tests and composition roots that own theirs).</summary>
    public IdentityProver(InfrastructureOptions options, HttpClient httpClient, TimeSpan? budget = null)
        : this(options, () => httpClient, budget)
    {
        Guard.IsNotNull(httpClient);
    }

    private IdentityProver(InfrastructureOptions options, Func<HttpClient> client, TimeSpan? budget)
    {
        Guard.IsNotNull(options);
        _keyFile = new IdentityKeyFile(options);
        _rootFp = IdentityProof.RootFingerprint(_keyFile.StateDirectory);
        _client = client;
        _budget = budget ?? DefaultBudget;
        Guard.IsGreaterThan(_budget, TimeSpan.Zero);
    }

    public async Task<IdentityProofFailure?> ProveAsync(Uri endpoint, CancellationToken ctx)
    {
        Guard.IsNotNull(endpoint);

        // Read-only first: a root with no key cannot prove, and nothing is created to change that.
        if (_keyFile.Read() is not { } trustedKey)
        {
            return IdentityProofFailure.NoKey;
        }

        var nonce = IdentityProof.NewNonce();
        var keyId = IdentityProof.KeyId(trustedKey);
        var port = endpoint.Port;
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ctx);
        bound.CancelAfter(_budget);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(endpoint, IdentityProof.EndpointPath))
            {
                Content = ChallengeContent(nonce, _rootFp, keyId)
            };
            using var response = await _client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                bound.Token);

            return response.IsSuccessStatusCode
                ? await VerifyAsync(response, trustedKey, nonce, port, bound.Token)
                : await NotProvenByStatusAsync(response, bound.Token);
        }
        catch (OperationCanceledException) when (!ctx.IsCancellationRequested)
        {
            return IdentityProofFailure.Timeout;
        }
        catch (HttpRequestException)
        {
            // Nothing answered: from a client's perspective the attempt produced no proof.
            return IdentityProofFailure.NonSuccessStatus;
        }
    }

    /// <summary>Verifies the frozen response body against the transcript built from the dialled port.</summary>
    private async Task<IdentityProofFailure?> VerifyAsync(HttpResponseMessage response, ECDsa trustedKey,
        string nonce, int port, CancellationToken cancellationToken)
    {
        if (await ReadBoundedAsync(response.Content, cancellationToken) is not { } body)
        {
            return IdentityProofFailure.Malformed;
        }

        try
        {
            var proof = JsonSerializer.Deserialize<ProofBody>(body);
            return proof is not { V: 1, KeyId: not null, Signature: not null }
                ? IdentityProofFailure.Malformed
                : IdentityProof.Verify(trustedKey, nonce, _rootFp, port, proof.KeyId, proof.Signature);
        }
        catch (JsonException)
        {
            return IdentityProofFailure.Malformed;
        }
    }

    /// <summary>The frozen 400s carry their reason; every other status is just non-success.</summary>
    private static async Task<IdentityProofFailure> NotProvenByStatusAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest
            || await ReadBoundedAsync(response.Content, cancellationToken) is not { } body)
        {
            return IdentityProofFailure.NonSuccessStatus;
        }

        try
        {
            return JsonSerializer.Deserialize<ErrorBody>(body)?.Error switch
            {
                "root-mismatch" => IdentityProofFailure.RootMismatch,
                "no-key" => IdentityProofFailure.NoKey,
                "malformed" => IdentityProofFailure.Malformed,
                _ => IdentityProofFailure.NonSuccessStatus
            };
        }
        catch (JsonException)
        {
            return IdentityProofFailure.NonSuccessStatus;
        }
    }

    private static ByteArrayContent ChallengeContent(string nonce, string rootFp, string keyId)
    {
        var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(
            new ChallengeBody(1, nonce, rootFp, keyId)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>The body, or null when it is missing, unreadable, or larger than the wire allows.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > ResponseByteBound)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > ResponseByteBound)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed record ChallengeBody(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("rootFp")] string RootFp,
        [property: JsonPropertyName("keyId")] string KeyId);

    private sealed record ProofBody
    {
        [JsonPropertyName("v")] public int V { get; init; }

        [JsonPropertyName("keyId")] public string? KeyId { get; init; }

        [JsonPropertyName("signature")] public string? Signature { get; init; }
    }

    private sealed record ErrorBody(
        [property: JsonPropertyName("error")] string Error);
}
