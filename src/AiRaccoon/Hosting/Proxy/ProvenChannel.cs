using AiRaccoon.Hosting.Common;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Hosting.Proxy;

/// <summary>
///     A proof's verdict together with the connection it rode on. <see cref="Client" /> is set only
///     when the listener proved, and reaches that listener and no other: a request sent through it
///     rides the proven connection or fails. Dispose it once done.
/// </summary>
public sealed class ProvenChannel : IDisposable
{
    private ProvenChannel(HttpClient? client, IdentityProofFailure? failure)
    {
        Client = client;
        Failure = failure;
    }

    /// <summary>The client bound to the proven connection; null when the listener did not prove.</summary>
    public HttpClient? Client { get; }

    /// <summary>Why the listener did not prove; null when it did.</summary>
    public IdentityProofFailure? Failure { get; }

    public void Dispose() => Client?.Dispose();

    public static ProvenChannel Proven(HttpClient client)
    {
        Guard.IsNotNull(client);
        return new ProvenChannel(client, null);
    }

    public static ProvenChannel NotProven(IdentityProofFailure failure) => new(null, failure);
}
