using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiRaccoon.Hosting.Common;

namespace AiRaccoon.Setup.Identity;

/// <summary>
///     Maps POST /identity/prove (ADR-0106 D2): the one pre-token route, on the existing loopback
///     listener. It signs the frozen transcript with the cached signer <c>serve</c> minted into this
///     root's state directory, so a client can verify the listener's identity before any secret
///     moves. Read-only by construction — nothing here mints or heals a key.
/// </summary>
internal static class IdentityProofEndpoint
{
    /// <summary>A challenge is a few hundred bytes; anything larger is refused without being read.</summary>
    private const int MaxChallengeBytes = 8 * 1024;

    /// <summary>A cheap bound on the unauthenticated ECDSA oracle: a burst beyond this waits briefly, then 503s.</summary>
    private const int MaxInFlight = 4;

    private static readonly TimeSpan InFlightWait = TimeSpan.FromSeconds(1);

    extension(WebApplication webApplication)
    {
        internal void MapIdentityProof(IdentityKeyFile keyFile)
        {
            var inFlight = new SemaphoreSlim(MaxInFlight, MaxInFlight);
            webApplication.MapPost(IdentityProof.EndpointPath,
                (Delegate)((HttpContext context) => RespondAsync(context, keyFile, inFlight)));
        }
    }

    private static async Task<IResult> RespondAsync(HttpContext context, IdentityKeyFile keyFile,
        SemaphoreSlim inFlight)
    {
        if (!await inFlight.WaitAsync(InFlightWait, context.RequestAborted))
        {
            return Results.Json(new ErrorBody("busy"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var challenge = await ReadChallengeAsync(context.Request, context.RequestAborted);
            if (challenge is null)
            {
                return Refuse("malformed");
            }

            // A listener serves one root, and the answer proves only that root: a challenge for
            // another one is refused, and neither fingerprint goes back over the wire (F12).
            var rootFp = IdentityProof.RootFingerprint(keyFile.StateDirectory);
            if (!string.Equals(challenge.RootFp, rootFp, StringComparison.Ordinal))
            {
                return Refuse("root-mismatch");
            }

            if (keyFile.Read() is not { } signer)
            {
                return Refuse("no-key");
            }

            // The transcript binds the port this listener actually holds, so a signature relayed
            // from a same-key backend on another port fails the verifier's reconstruction (F1).
            // keyId is the requester's hint; the answer always carries this listener's own keyId,
            // so a mismatch fails the verifier's pin instead of inventing a fourth error value.
            var keyId = IdentityProof.KeyId(signer);
            var signature = IdentityProof.Sign(signer, challenge.Nonce!, keyId, rootFp,
                context.Connection.LocalPort);
            return Results.Json(new ProofBody(1, keyId, signature));
        }
        finally
        {
            inFlight.Release();
        }
    }

    private static IResult Refuse(string error) =>
        Results.Json(new ErrorBody(error), statusCode: StatusCodes.Status400BadRequest);

    /// <summary>The parsed challenge, or null for a body that is not the frozen request shape.</summary>
    private static async Task<Challenge?> ReadChallengeAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxChallengeBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxChallengeBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        try
        {
            var challenge = JsonSerializer.Deserialize<Challenge>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            return IsWellFormed(challenge) ? challenge : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The frozen request shape: v 1, a 32-byte base64url nonce, a rootFp and a keyId.</summary>
    private static bool IsWellFormed(Challenge? challenge) =>
        challenge is { V: 1, Nonce: not null, RootFp: not null, KeyId: not null }
        && challenge.RootFp.Length > 0
        && challenge.KeyId.Length > 0
        && Base64Url.IsValid(challenge.Nonce)
        && Base64Url.DecodeFromChars(challenge.Nonce).Length == IdentityProof.NonceBytes;

    private sealed record Challenge
    {
        [JsonPropertyName("v")] public int V { get; init; }

        [JsonPropertyName("nonce")] public string? Nonce { get; init; }

        [JsonPropertyName("rootFp")] public string? RootFp { get; init; }

        [JsonPropertyName("keyId")] public string? KeyId { get; init; }
    }

    private sealed record ProofBody(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("keyId")] string KeyId,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record ErrorBody(
        [property: JsonPropertyName("error")] string Error);
}
